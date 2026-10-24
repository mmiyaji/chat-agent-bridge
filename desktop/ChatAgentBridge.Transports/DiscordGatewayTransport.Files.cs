using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class DiscordGatewayTransport
{
    private static readonly HashSet<string> DiscordCdnHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.discordapp.com", "media.discordapp.net", "cdn.discordapp.net", "media.discordapp.com"
    };

    public async Task<IReadOnlyList<IncomingChatFile>> ResolveIncomingFilesAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var message = await ReadOriginalMessageAsync(request, cancellationToken).ConfigureAwait(false);
        return message.Attachments?.Select(ToIncomingFile).Take(TransportHttp.MaxIncomingFileCount).ToArray()
            ?? Array.Empty<IncomingChatFile>();
    }

    public async Task DownloadFileAsync(ChatRequest request, IncomingChatFile file, Stream destination,
        long maxBytes, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var message = await ReadOriginalMessageAsync(request, cancellationToken).ConfigureAwait(false);
        var attachment = message.Attachments?.FirstOrDefault(item => string.Equals(item.Id, file.Id, StringComparison.Ordinal));
        if (attachment is null || !TryValidateCdnUrl(attachment.Url, out var url) ||
            attachment.Size < 0 || attachment.Size > maxBytes)
            throw new InvalidOperationException("The Discord attachment is no longer available or exceeds the permitted size.");

        // CDN requests intentionally carry no bot Authorization header and reject all redirects.
        using var cdnRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _cdnHttp.SendAsync(cdnRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("Discord attachment redirects are not allowed.");
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != url)
            throw new InvalidOperationException("Discord attachment redirects are not allowed.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Discord attachment download failed with HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new InvalidOperationException("Discord attachment exceeds the permitted size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (total > maxBytes - read) throw new InvalidOperationException("Discord attachment exceeds the permitted size.");
            total += read;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total != attachment.Size)
            throw new InvalidOperationException("Discord attachment size changed while it was being downloaded.");
    }

    public async Task<IReadOnlyList<string>> UploadFilesAsync(ChatRequest request,
        IReadOnlyList<PreparedFileAttachment> files, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await TransportHttp.ValidatePreparedFilesAsync(files, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("{\"allowed_mentions\":{\"parse\":[],\"users\":[],\"roles\":[],\"replied_user\":false}}"), "payload_json");
            for (int index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var part = new StreamContent(file.OpenRead());
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, $"files[{index}]", file.FileName);
            }
            using var message = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_apiBase, $"channels/{Uri.EscapeDataString(request.ThreadId)}/messages")) { Content = form };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", _token);
            try
            {
                var result = await SendMultipartMessageAsync(message, cancellationToken).ConfigureAwait(false);
                return result.Attachments?.Select(item => item.Url ?? item.ProxyUrl).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>().Take(files.Count).ToArray() ?? Array.Empty<string>();
            }
            catch (DiscordUploadRateLimitException ex) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(ex.RetryAfterSeconds, 0.05, 5)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<DiscordIncomingMessage> ReadOriginalMessageAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string channelId = request.SourceChannelId ?? request.ThreadId;
        if (channelId != request.ChannelId && channelId != request.ThreadId)
            throw new InvalidOperationException("Discord attachment source channel did not match the request scope.");
        if (request.GuildId is not null)
        {
            var source = await SendApiAsync<DiscordChannelInfo>(HttpMethod.Get, $"channels/{Uri.EscapeDataString(channelId)}", null, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(source.Id, channelId, StringComparison.Ordinal) ||
                !string.Equals(source.GuildId, request.GuildId, StringComparison.Ordinal) ||
                channelId == request.ThreadId && channelId != request.ChannelId &&
                    (source.Type is not (10 or 11 or 12) || !string.Equals(source.ParentId, request.ChannelId, StringComparison.Ordinal)) ||
                channelId == request.ChannelId && !string.Equals(source.Id, request.ChannelId, StringComparison.Ordinal))
                throw new InvalidOperationException("Discord attachment source channel did not match the original request scope.");
        }
        var response = await ReadIncomingMessageMetadataAsync(channelId, request.MessageId, cancellationToken).ConfigureAwait(false);
        string? author = response.Author?.Id;
        if (!string.Equals(response.Id, request.MessageId, StringComparison.Ordinal) ||
            !string.Equals(response.ChannelId, channelId, StringComparison.Ordinal) ||
            !string.Equals(author, request.UserId, StringComparison.Ordinal) ||
            response.GuildId is not null && !string.Equals(response.GuildId, request.GuildId, StringComparison.Ordinal))
            throw new InvalidOperationException("Discord attachment message identity did not match the original request.");
        return response;
    }

    private async Task<DiscordIncomingMessage> ReadIncomingMessageMetadataAsync(string channelId, string messageId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_apiBase, $"channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Discord API request failed with HTTP {(int)response.StatusCode}.");
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
            throw new InvalidOperationException("Discord attachment metadata redirects are not allowed.");
        const int maxMetadataBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxMetadataBytes)
            throw new InvalidOperationException("Discord returned oversized attachment metadata.");
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (bounded.Length > maxMetadataBytes - read) throw new InvalidOperationException("Discord returned oversized attachment metadata.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        bounded.Position = 0;
        try { return await JsonSerializer.DeserializeAsync<DiscordIncomingMessage>(bounded, TransportHttp.JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Discord returned empty attachment metadata."); }
        catch (JsonException) { throw new InvalidOperationException("Discord returned invalid attachment metadata."); }
    }

    private async Task<DiscordIncomingMessage> SendMultipartMessageAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == (HttpStatusCode)429)
                throw new DiscordUploadRateLimitException(ReadRetryAfter(response));
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Discord file upload failed with HTTP {(int)response.StatusCode}.");
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
                throw new InvalidOperationException("Discord upload redirects are not allowed.");
            const int maxResponseBytes = 1024 * 1024;
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxResponseBytes)
                throw new InvalidOperationException("Discord returned oversized upload metadata.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length > maxResponseBytes - read) throw new InvalidOperationException("Discord returned oversized upload metadata.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync<DiscordIncomingMessage>(buffer, TransportHttp.JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Discord returned an empty upload response.");
        }
    }

    private static bool TryValidateCdnUrl(string? value, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps ||
            !DiscordCdnHosts.Contains(candidate.IdnHost) || !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Fragment) || candidate.Port != 443) return false;
        url = candidate;
        return true;
    }

    private static IncomingChatFile ToIncomingFile(DiscordAttachment attachment) =>
        new(attachment.Id ?? "", attachment.Filename ?? "attachment", attachment.Size, attachment.ContentType);

    private sealed class DiscordUploadRateLimitException(double retryAfterSeconds) : Exception
    { public double RetryAfterSeconds { get; } = retryAfterSeconds; }

    private sealed record DiscordIncomingMessage(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("guild_id")] string? GuildId,
        [property: JsonPropertyName("author")] DiscordAuthor? Author,
        [property: JsonPropertyName("attachments")] DiscordAttachment[]? Attachments);
    private sealed record DiscordAuthor([property: JsonPropertyName("id")] string? Id);
    private sealed record DiscordAttachment(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("content_type")] string? ContentType,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("proxy_url")] string? ProxyUrl);
}
