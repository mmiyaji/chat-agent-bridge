using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class SlackSocketModeTransport
{
    private const string SlackFilesHost = "files.slack.com";

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
