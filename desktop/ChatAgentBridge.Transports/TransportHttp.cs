using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports;

internal static class TransportHttp
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static HttpClient CreateClient(bool bypassProxy = false, IWebProxy? proxy = null, TimeSpan? timeout = null)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = !bypassProxy,
            Proxy = bypassProxy ? null : proxy,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    internal static void ConfigureMattermostWebSocketProxy(ClientWebSocketOptions options, bool bypassProxy, IWebProxy? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Null explicitly disables proxying in ClientWebSocket; a non-null value selects the configured proxy.
        options.Proxy = bypassProxy ? null : proxy ?? HttpClient.DefaultProxy;
    }

    internal static Uri MattermostBaseUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host))) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Mattermost URL must use HTTPS (HTTP is allowed only for localhost).");
        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
    }

    internal static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    internal static HttpRequestMessage AuthenticatedRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<T> SendJsonAsync<T>(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Chat service request failed with HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Chat service returned an empty response.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Chat service returned an invalid response.");
            }
        }
    }

    internal static StringContent JsonContent<T>(T value) => new(
        JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    internal const int MaxUploadFileCount = 10;
    internal const long MaxUploadBytes = 100L * 1024 * 1024;

    internal static async Task ValidatePreparedFilesAsync(IReadOnlyList<PreparedFileAttachment> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 1 or > MaxUploadFileCount)
            throw new InvalidOperationException("File uploads require between 1 and 10 files.");
        long totalLength = 0;
        foreach (var file in files)
        {
            if (file is null) throw new InvalidOperationException("The prepared file batch is invalid.");
            ValidateUploadFileName(file.FileName);
            if (file.Length < 0 || file.Length > MaxUploadBytes || totalLength > MaxUploadBytes - file.Length)
                throw new InvalidOperationException("The file upload batch exceeds the 100 MiB limit.");
            totalLength += file.Length;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = file.OpenRead();
            if (stream is null || !stream.CanRead) throw new InvalidOperationException("A prepared file could not be read.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (length > file.Length) throw new InvalidOperationException("A prepared file length changed before upload.");
                hash.AppendData(buffer, 0, read);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (length != file.Length || !string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A prepared file failed its length or integrity check.");
        }
    }

    internal static void ValidateUploadFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\', '<', '>', ':', '"', '|', '?', '*']) >= 0 || fileName.Any(char.IsControl))
            throw new InvalidOperationException("A file name is not safe to upload.");
    }
}
