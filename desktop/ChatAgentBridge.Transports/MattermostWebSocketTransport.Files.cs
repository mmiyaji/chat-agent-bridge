using System.Net.Http.Headers;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

public sealed partial class MattermostWebSocketTransport
{
    public async Task<IReadOnlyList<IncomingChatFile>> ResolveIncomingFilesAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ValidateIncomingFileRequest(request);
        EnsureIncomingFileAccess(request);
        if (request.IncomingFilesError is not null) throw new InvalidOperationException("Mattermost attachments could not be processed safely.");
        if (request.IncomingFiles.Count > TransportHttp.MaxIncomingFileCount) throw new InvalidOperationException("Mattermost requests may contain at most five attachments.");
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        var resolved = new List<IncomingChatFile>(request.IncomingFiles.Count);
        MattermostIncomingPostContext? post = null;
        foreach (var candidate in request.IncomingFiles)
        {
            EnsureIncomingFileConnection(request, generation, cancellationToken);
            ValidateMattermostFileId(candidate.Id);
            var info = await GetIncomingFileInfoAsync(candidate.Id, _token!, cancellationToken).ConfigureAwait(false);
            ValidateMattermostFileMetadata(request, candidate.Id, info);
            // All files in this preflight share the same original post. Downloads still fetch
            // fresh membership/metadata immediately before each transfer.
            post ??= await GetIncomingPostContextAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateMattermostFileAssociation(request, candidate.Id, post);
            EnsureIncomingFileAccess(request);
            resolved.Add(new IncomingChatFile(info.Id!, info.Name!, info.Size, info.MimeType));
        }
        EnsureIncomingFileConnection(request, generation, cancellationToken);
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
            throw new InvalidOperationException("Mattermost file does not belong to this request.");
        var generation = Volatile.Read(ref _fileUploadConnectionGeneration);
        ValidateMattermostFileId(file.Id);
        var info = await GetIncomingFileInfoAsync(file.Id, _token!, cancellationToken).ConfigureAwait(false);
        await ValidateMattermostFileAssociationAsync(request, file.Id, info, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(info.Name, file.Name, StringComparison.Ordinal) || info.Size != file.Size)
            throw new InvalidOperationException("Mattermost file metadata changed after resolution.");
        if (info.Size > maxBytes) throw new InvalidOperationException("Mattermost file exceeds the allowed download size.");
        EnsureIncomingFileConnection(request, generation, cancellationToken);
        EnsureIncomingFileAccess(request);
        var uri = new Uri(_serverUri!, $"api/v4/files/{Uri.EscapeDataString(file.Id)}");
        using var downloadRequest = TransportHttp.AuthenticatedRequest(HttpMethod.Get, uri, _token!);
        using var response = await ActiveFileUploadHttp.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
            throw new InvalidOperationException("Mattermost redirected the file download to an unexpected destination.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Mattermost file download failed with HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidOperationException("Mattermost file exceeds the allowed download size.");
        await CopyIncomingFileBoundedAsync(response.Content, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        EnsureIncomingFileConnection(request, generation, cancellationToken);
        EnsureIncomingFileAccess(request);
    }

    private async Task<MattermostIncomingFileInfo> GetIncomingFileInfoAsync(string id, string token, CancellationToken cancellationToken)
    {
        var request = TransportHttp.AuthenticatedRequest(HttpMethod.Get,
            new Uri(_serverUri!, $"api/v4/files/{Uri.EscapeDataString(id)}/info"), token);
        return await TransportHttp.SendBoundedJsonAsync<MattermostIncomingFileInfo>(ActiveFileUploadHttp, request, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateIncomingFileRequest(ChatRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, "mattermost", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Request provider must be Mattermost.", nameof(request));
        EnsureConnectedForProgress();
        if (!_fileUploadConnected || !string.Equals(request.ChannelId, _channelId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.ThreadId) || string.IsNullOrWhiteSpace(request.MessageId))
            throw new InvalidOperationException("Mattermost file request does not match the active connection.");
    }

    private void EnsureIncomingFileAccess(ChatRequest request)
    {
        var settings = Volatile.Read(ref _liveAccessSettings);
        if (settings is not null && !ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", request.UserId, request.ChannelId, request.ThreadId))
            throw new InvalidOperationException("Mattermost access to this request was revoked.");
    }

    private void EnsureIncomingFileConnection(ChatRequest request, long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnectedForProgress();
        if (!_fileUploadConnected || generation != Volatile.Read(ref _fileUploadConnectionGeneration) ||
            !string.Equals(request.ChannelId, _channelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost connection changed during file download.");
    }

    private async Task ValidateMattermostFileAssociationAsync(ChatRequest request, string id, MattermostIncomingFileInfo info, CancellationToken cancellationToken)
    {
        ValidateMattermostFileMetadata(request, id, info);
        var post = await GetIncomingPostContextAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateMattermostFileAssociation(request, id, post);
    }

    private static void ValidateMattermostFileMetadata(ChatRequest request, string id, MattermostIncomingFileInfo info)
    {
        if (!string.Equals(info.Id, id, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(info.Name) || info.Size < 0 ||
            !string.Equals(info.PostId, request.MessageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mattermost file is not attached to the approved request message.");
    }

    private async Task<MattermostIncomingPostContext> GetIncomingPostContextAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var postRequest = TransportHttp.AuthenticatedRequest(HttpMethod.Get,
            new Uri(_serverUri!, $"api/v4/posts/{Uri.EscapeDataString(request.MessageId)}"), _token!);
        return await TransportHttp.SendBoundedJsonAsync<MattermostIncomingPostContext>(ActiveFileUploadHttp, postRequest, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMattermostFileAssociation(ChatRequest request, string id, MattermostIncomingPostContext post)
    {
        var rootId = string.IsNullOrWhiteSpace(post.RootId) ? post.Id : post.RootId;
        if (!string.Equals(post.Id, request.MessageId, StringComparison.Ordinal) ||
            !string.Equals(post.ChannelId, request.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(post.UserId, request.UserId, StringComparison.Ordinal) ||
            !string.Equals(rootId, request.ThreadId, StringComparison.Ordinal) ||
            post.FileIds is not { ValueKind: System.Text.Json.JsonValueKind.Array } ids ||
            !ids.EnumerateArray().Any(fileId => fileId.ValueKind == System.Text.Json.JsonValueKind.String && fileId.GetString() == id))
            throw new InvalidOperationException("Mattermost file is not attached to the approved request message.");
    }

    private static void ValidateMattermostFileId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidOperationException("Mattermost returned an invalid file identifier.");
    }

    private static async Task CopyIncomingFileBoundedAsync(HttpContent content, Stream destination, long maxBytes, CancellationToken cancellationToken)
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

    private sealed record MattermostIncomingFileInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("mime_type")] string? MimeType,
        [property: System.Text.Json.Serialization.JsonPropertyName("post_id")] string? PostId);

    private sealed record MattermostIncomingPostContext(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("channel_id")] string? ChannelId,
        [property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId,
        [property: System.Text.Json.Serialization.JsonPropertyName("root_id")] string? RootId,
        [property: System.Text.Json.Serialization.JsonPropertyName("file_ids")] System.Text.Json.JsonElement? FileIds);

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
