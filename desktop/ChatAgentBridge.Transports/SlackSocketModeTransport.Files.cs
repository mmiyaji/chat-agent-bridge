using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class SlackSocketModeTransport
{
    private const string SlackFilesHost = "files.slack.com";

    private static IReadOnlyList<IncomingChatFile> ReadIncomingFiles(JsonElement evt, out string? error)
    {
        error = null;
        if (!evt.TryGetProperty("files", out var files)) return Array.Empty<IncomingChatFile>();
        if (files.ValueKind != JsonValueKind.Array) { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
        var result = new List<IncomingChatFile>();
        foreach (var item in files.EnumerateArray())
        {
            if (result.Count == TransportHttp.MaxIncomingFileCount)
            { error = "too_many_attachments"; return result; }
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idValue.GetString()) ||
                !item.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString()) ||
                !item.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size < 0)
            { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
            if (result.Any(file => string.Equals(file.Id, idValue.GetString(), StringComparison.Ordinal)))
            { error = "invalid_attachment_metadata"; return Array.Empty<IncomingChatFile>(); }
            var contentType = item.TryGetProperty("mimetype", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString() : null;
            result.Add(new IncomingChatFile(idValue.GetString()!, nameValue.GetString()!, size, contentType));
        }
        return result;
    }

    public async Task<IReadOnlyList<IncomingChatFile>> ResolveIncomingFilesAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ValidateIncomingFileRequest(request);
        EnsureIncomingFileAccess(request);
        if (request.IncomingFilesError is not null) throw new InvalidOperationException("Slack attachments could not be processed safely.");
        if (request.IncomingFiles.Count > TransportHttp.MaxIncomingFileCount) throw new InvalidOperationException("Slack requests may contain at most five attachments.");
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        var resolved = new List<IncomingChatFile>(request.IncomingFiles.Count);
        foreach (var candidate in request.IncomingFiles)
        {
            EnsureIncomingFileConnection(generation, cancellationToken);
            ValidateSlackFileId(candidate.Id);
            var response = await GetSlackIncomingFileInfoAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            var file = response.File;
            if (!response.Ok || file is null || !string.Equals(file.Id, candidate.Id, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(file.Name) || file.Size < 0 || !IsHostedSlackFile(file) || !IsSharedOnRequestMessage(file.Shares, request) ||
                !TryGetSlackDownloadUri(file, out _))
                throw new InvalidOperationException("Slack file is not shared with the approved request message.");
            resolved.Add(new IncomingChatFile(file.Id!, file.Name!, file.Size, file.Mimetype));
        }
        EnsureIncomingFileConnection(generation, cancellationToken);
        return resolved;
    }

    public async Task DownloadFileAsync(ChatRequest request, IncomingChatFile file, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        ValidateIncomingFileRequest(request);
        EnsureIncomingFileAccess(request);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (request.IncomingFilesError is not null || !request.IncomingFiles.Any(item => string.Equals(item.Id, file.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException("Slack file does not belong to this request.");
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        ValidateSlackFileId(file.Id);
        var metadata = await GetSlackIncomingFileInfoAsync(file.Id, cancellationToken).ConfigureAwait(false);
        var info = metadata.File;
        if (!metadata.Ok || info is null || !string.Equals(info.Id, file.Id, StringComparison.Ordinal) ||
            !string.Equals(info.Name, file.Name, StringComparison.Ordinal) || info.Size != file.Size ||
            !IsHostedSlackFile(info) || !IsSharedOnRequestMessage(info.Shares, request) || !TryGetSlackDownloadUri(info, out var downloadUri))
            throw new InvalidOperationException("Slack file is not shared with the approved request message.");
        if (info.Size > maxBytes) throw new InvalidOperationException("Slack file exceeds the allowed download size.");
        EnsureIncomingFileConnection(generation, cancellationToken);
        EnsureIncomingFileAccess(request);
        using var downloadRequest = TransportHttp.AuthenticatedRequest(HttpMethod.Get, downloadUri!, _botToken!);
        using var response = await _fileUploadHttp.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } finalUri && finalUri != downloadUri)
            throw new InvalidOperationException("Slack redirected the file download to an unapproved destination.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Slack file download failed.");
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidOperationException("Slack file exceeds the allowed download size.");
        await CopyBoundedAsync(response.Content, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        EnsureIncomingFileConnection(generation, cancellationToken);
        EnsureIncomingFileAccess(request);
    }

    private void ValidateIncomingFileRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, "slack", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Request provider must be Slack.", nameof(request));
        if (!_connected || !_fileUploadConnected || _botToken is null) throw new InvalidOperationException("Slack is not connected.");
        if (string.IsNullOrWhiteSpace(request.ChannelId) || string.IsNullOrWhiteSpace(request.ThreadId) || string.IsNullOrWhiteSpace(request.MessageId))
            throw new ArgumentException("Slack file downloads require a channel, thread, and message.", nameof(request));
    }

    private void EnsureIncomingFileAccess(ChatRequest request)
    {
        var settings = Volatile.Read(ref _liveAccessSettings);
        if (settings is not null && !ChatAccessPolicy.IsAllowedRequest(settings, "slack", request.UserId, request.ChannelId, request.ThreadId))
            throw new InvalidOperationException("Slack access to this request was revoked.");
    }

    private async Task<SlackIncomingFileInfoResponse> GetSlackIncomingFileInfoAsync(string id, CancellationToken cancellationToken)
    {
        var uri = new Uri(_apiBase, $"files.info?file={Uri.EscapeDataString(id)}");
        var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get, uri, _botToken!);
        var response = await TransportHttp.SendBoundedJsonAsync<SlackIncomingFileInfoResponse>(_http, request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok && string.Equals(response.Error, "missing_scope", StringComparison.Ordinal))
            throw new InvalidOperationException("Slack incoming attachments require the files:read scope.");
        return response;
    }

    private void EnsureIncomingFileConnection(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_connected || !_fileUploadConnected || _botToken is null || generation != Volatile.Read(ref _fileUploadConnectionGeneration))
            throw new InvalidOperationException("Slack connection changed during file download.");
    }

    private static bool IsSharedOnRequestMessage(JsonElement? shares, ChatRequest request)
    {
        if (shares is not { ValueKind: JsonValueKind.Object } value) return false;
        foreach (var visibility in new[] { "private", "public" })
        {
            if (!value.TryGetProperty(visibility, out var channels) || channels.ValueKind != JsonValueKind.Object ||
                !channels.TryGetProperty(request.ChannelId, out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
            foreach (var message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String && ts.GetString() == request.MessageId &&
                    (message.TryGetProperty("thread_ts", out var thread)
                        ? thread.ValueKind == JsonValueKind.String && thread.GetString() == request.ThreadId
                        : string.Equals(request.MessageId, request.ThreadId, StringComparison.Ordinal)))
                    return true;
        }
        return false;
    }

    private static bool TryGetSlackDownloadUri(SlackIncomingFile file, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(file.UrlPrivateDownload ?? file.UrlPrivate, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || !string.Equals(candidate.Host, SlackFilesHost, StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort && candidate.Port != 443 || !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment)) return false;
        uri = candidate;
        return true;
    }

    private static bool IsHostedSlackFile(SlackIncomingFile file) => file.IsExternal == false &&
        (file.Mode is "hosted" or "snippet") && !string.Equals(file.Filetype, "remote", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSlackFileId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidOperationException("Slack returned an invalid file identifier.");
    }

    private static async Task CopyBoundedAsync(HttpContent content, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            if (copied > maxBytes - read) throw new InvalidOperationException("Incoming file exceeds the allowed download size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }
    }

    private sealed record SlackIncomingFileInfoResponse([property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("file")] SlackIncomingFile? File,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record SlackIncomingFile([property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mimetype")] string? Mimetype, [property: JsonPropertyName("url_private_download")] string? UrlPrivateDownload,
        [property: JsonPropertyName("url_private")] string? UrlPrivate, [property: JsonPropertyName("shares")] JsonElement? Shares,
        [property: JsonPropertyName("is_external")] bool? IsExternal, [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("filetype")] string? Filetype);

    public async Task<IReadOnlyList<string>> UploadFilesAsync(
        ChatRequest request,
        IReadOnlyList<PreparedFileAttachment> files,
        CancellationToken cancellationToken)
    {
        ValidateFileUploadRequest(request);
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        await TransportHttp.ValidatePreparedFilesAsync(files, cancellationToken).ConfigureAwait(false);
        EnsureFileUploadConnection(generation, cancellationToken);

        var token = _botToken!;
        var completed = new List<SlackUploadFile>(files.Count);
        foreach (var file in files)
        {
            EnsureFileUploadConnection(generation, cancellationToken);
            var urlRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBase, "files.getUploadURLExternal"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["filename"] = file.FileName,
                    ["length"] = file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
            };
            urlRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            SlackUploadUrlResponse upload;
            using (urlRequest)
            using (var response = await _http.SendAsync(urlRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var apiError = await TryReadSlackApiErrorAsync(response, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(apiError, "missing_scope", StringComparison.Ordinal) || string.Equals(apiError, "not_allowed_token_type", StringComparison.Ordinal))
                        throw SlackFileApiError(apiError, "Slack could not prepare the file upload.");
                    throw new InvalidOperationException("Slack file upload URL request failed.");
                }
                upload = await ReadSlackUploadUrlResponseAsync(response, cancellationToken).ConfigureAwait(false);
            }
            if (!upload.Ok)
                throw SlackFileApiError(upload.Error, "Slack could not prepare the file upload.");
            if (string.IsNullOrWhiteSpace(upload.FileId) || !IsAllowedSlackUploadUri(upload.UploadUrl))
                throw new InvalidOperationException("Slack returned an invalid file upload destination.");

            EnsureFileUploadConnection(generation, cancellationToken);
            await using (var stream = file.OpenRead())
            using (var content = new StreamContent(stream))
            using (var rawUpload = new HttpRequestMessage(HttpMethod.Post, upload.UploadUrl!) { Content = content })
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = file.Length;
                using var response = await _fileUploadHttp.SendAsync(rawUpload, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Slack file data upload failed.");
            }
            completed.Add(new SlackUploadFile(upload.FileId!, file.FileName));
        }

        EnsureFileUploadConnection(generation, cancellationToken);
        var result = await SlackApiAsync<SlackCompleteUploadResponse>("files.completeUploadExternal", token,
            new { files = completed.Select(f => new { id = f.Id, title = f.Title }).ToArray(), channel_id = request.ChannelId, thread_ts = request.ThreadId },
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
            throw SlackFileApiError(result.Error, "Slack could not complete the file upload.");
        if (result.Files is { } returnedFiles &&
            (returnedFiles.Length != completed.Count || completed.Any(file => !returnedFiles.Any(returned => string.Equals(returned.Id, file.Id, StringComparison.Ordinal)))))
            throw new InvalidOperationException("Slack returned an unexpected file list after upload.");
        return completed.Select(file => file.Id).ToArray();
    }

    private void ValidateFileUploadRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, "slack", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Slack.", nameof(request));
        if (!_connected || !_fileUploadConnected || _botToken is null)
            throw new InvalidOperationException("Slack is not connected.");
        if (string.IsNullOrWhiteSpace(request.ChannelId) || string.IsNullOrWhiteSpace(request.ThreadId))
            throw new ArgumentException("Slack file uploads require a channel and thread.", nameof(request));
    }

    private void EnsureFileUploadConnection(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_connected || !_fileUploadConnected || _botToken is null || generation != Volatile.Read(ref _fileUploadConnectionGeneration))
            throw new InvalidOperationException("Slack connection changed during file upload.");
    }

    private static bool IsAllowedSlackUploadUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, SlackFilesHost, StringComparison.OrdinalIgnoreCase) &&
            (uri.IsDefaultPort || uri.Port == 443) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }

    private static async Task<SlackUploadUrlResponse> ReadSlackUploadUrlResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync<SlackUploadUrlResponse>(stream, TransportHttp.JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Slack returned an empty file upload response.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Slack returned an invalid file upload response.");
        }
    }

    private static InvalidOperationException SlackFileApiError(string? error, string fallback) =>
        string.Equals(error, "missing_scope", StringComparison.Ordinal) || string.Equals(error, "not_allowed_token_type", StringComparison.Ordinal)
            ? new InvalidOperationException("Slack file uploads require the bot token to have the files:write scope.")
            : new InvalidOperationException(fallback);

    private sealed record SlackUploadUrlResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("upload_url")] string? UploadUrl,
        [property: JsonPropertyName("file_id")] string? FileId,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record SlackCompleteUploadResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("files")] SlackCompletedFile[]? Files,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record SlackCompletedFile([property: JsonPropertyName("id")] string? Id);
    private sealed record SlackUploadFile(string Id, string Title);
}
