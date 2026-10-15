using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatAgentBridge.Transports;

internal static class TransportHttp
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static HttpClient CreateClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(20) };
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
}
