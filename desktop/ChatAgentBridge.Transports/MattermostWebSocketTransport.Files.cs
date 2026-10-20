using System.Net.Http.Headers;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport
{
    public async Task<IReadOnlyList<string>> UploadFilesAsync(
        ChatRequest request,
        IReadOnlyList<PreparedFileAttachment> files,
        CancellationToken cancellationToken)
    {
        ValidateFileUploadRequest(request);
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        await TransportHttp.ValidatePreparedFilesAsync(files, cancellationToken).ConfigureAwait(false);
        EnsureFileUploadConnection(request, generation, cancellationToken);

        var root = _serverUri!;
        var token = _token!;
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(request.ChannelId), "channel_id");
        foreach (var file in files)
        {
            var stream = file.OpenRead();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = file.Length;
            multipart.Add(content, "files", file.FileName);
        }

        EnsureFileUploadConnection(request, generation, cancellationToken);
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/files")) { Content = multipart };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploadResponse = await ActiveFileUploadHttp.SendAsync(uploadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mattermost file upload failed with HTTP {(int)uploadResponse.StatusCode}.");
        var fileIds = await ReadAndValidateMattermostFilesAsync(uploadResponse, request.ChannelId, files, cancellationToken).ConfigureAwait(false);

        EnsureFileUploadConnection(request, generation, cancellationToken);
        using var postBody = TransportHttp.JsonContent(new { channel_id = request.ChannelId, root_id = request.ThreadId, message = "", file_ids = fileIds });
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v4/posts")) { Content = postBody };
        postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var postResponse = await ActiveHttp.SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!postResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mattermost could not attach the uploaded files to the thread (HTTP {(int)postResponse.StatusCode}).");
        var created = await ReadMattermostPostAsync(postResponse, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(created.ChannelId, request.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(string.IsNullOrWhiteSpace(created.RootId) ? created.Id : created.RootId, request.ThreadId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost returned a post outside the requested thread.");
        return fileIds;
    }

    private void ValidateFileUploadRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, "mattermost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Request provider must be Mattermost.", nameof(request));
        EnsureConnectedForProgress();
        if (!_fileUploadConnected)
            throw new InvalidOperationException("Mattermost is reconnecting.");
        if (!string.Equals(request.ChannelId, _channelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost file upload request does not match the active channel.");
        if (string.IsNullOrWhiteSpace(request.ThreadId))
            throw new ArgumentException("Mattermost file uploads require a thread.", nameof(request));
    }

    private void EnsureFileUploadConnection(ChatRequest request, long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnectedForProgress();
        if (!_fileUploadConnected || generation != Volatile.Read(ref _fileUploadConnectionGeneration) ||
            !string.Equals(request.ChannelId, _channelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost connection changed during file upload.");
    }

    private static async Task<string[]> ReadAndValidateMattermostFilesAsync(
        HttpResponseMessage response,
        string expectedChannel,
        IReadOnlyList<PreparedFileAttachment> expectedFiles,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new InvalidOperationException("Mattermost returned an invalid file upload response."); }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("file_infos", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() != expectedFiles.Count)
                throw new InvalidOperationException("Mattermost returned an unexpected file list after upload.");
            var ids = new string[expectedFiles.Count];
            var index = 0;
            foreach (var info in infos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object || !TryJsonString(info, "id", out var id) ||
                    string.IsNullOrWhiteSpace(id) || ids.Take(index).Contains(id, StringComparer.Ordinal))
                    throw new InvalidOperationException("Mattermost returned invalid file metadata.");
                if (info.TryGetProperty("name", out var name) &&
                    (name.ValueKind != JsonValueKind.String || !string.Equals(name.GetString(), expectedFiles[index].FileName, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Mattermost returned file metadata that did not match the upload.");
                if (info.TryGetProperty("size", out var size) &&
                    (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var actualSize) || actualSize != expectedFiles[index].Length))
                    throw new InvalidOperationException("Mattermost returned file metadata that did not match the upload.");
                if (info.TryGetProperty("channel_id", out var channel) && channel.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(channel.GetString()) && !string.Equals(channel.GetString(), expectedChannel, StringComparison.Ordinal))
                    throw new InvalidOperationException("Mattermost returned file metadata for another channel.");
                if (info.TryGetProperty("channel_id", out channel) && channel.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Mattermost returned invalid file metadata.");
                ids[index++] = id;
            }
            return ids;
        }
    }

    private static bool TryJsonString(JsonElement element, string name, out string? value)
    {
        value = null;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString());
    }
}
