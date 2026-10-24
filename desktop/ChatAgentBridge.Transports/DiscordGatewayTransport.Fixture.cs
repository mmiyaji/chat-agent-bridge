#if CAB_DISCORD_FIXTURE
using System.Net;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

public sealed partial class DiscordGatewayTransport
{
    // This factory and its CDN rewrite handler are absent from production assemblies.
    public static DiscordGatewayTransport CreateLoopbackFixture(Uri endpoint, SessionStore sessions)
    {
        if (endpoint.Scheme != "http" || !IPAddress.TryParse(endpoint.Host, out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("The isolated Discord fixture must be a loopback HTTP origin.", nameof(endpoint));
        return new DiscordGatewayTransport(TransportHttp.CreateClient(bypassProxy: true),
            new Uri(endpoint, "api/v10/"), cdnHttp: new HttpClient(new FixtureCdnHandler(endpoint)), sessions: sessions);
    }

    private sealed class FixtureCdnHandler(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler
        { AllowAutoRedirect = false, UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get || request.Headers.Authorization is not null ||
                request.RequestUri is not { Scheme: "https", Host: "cdn.discordapp.com" } original)
                throw new InvalidOperationException("Unexpected isolated CDN request.");
            using var forwarded = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "cdn" + original.AbsolutePath));
            var response = await base.SendAsync(forwarded, token).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
    }
}
#endif
