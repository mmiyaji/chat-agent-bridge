using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostProxyTests
{
    [Fact]
    public async Task Http_client_uses_configured_proxy_by_default_and_bypasses_it_when_requested()
    {
        await using var server = new HttpEndpoint("{\"ok\":true}");
        await using var proxy = new HttpEndpoint("proxy", HttpStatusCode.BadGateway);
        var rejectingProxy = new FixedProxy(proxy.Uri);

        using (var proxied = TransportHttp.CreateClient(proxy: rejectingProxy))
        {
            var response = await proxied.GetAsync(server.Uri);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        Assert.Equal(1, proxy.RequestCount);
        Assert.Equal(0, server.RequestCount);

        using (var direct = TransportHttp.CreateClient(bypassProxy: true, proxy: rejectingProxy))
        {
            var response = await direct.GetAsync(server.Uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(1, proxy.RequestCount);
    }

    [Fact]
    public async Task Mattermost_username_lookup_can_reach_loopback_without_using_the_proxy()
    {
        const string body = "{\"id\":\"0123456789abcdefghijklmnop\",\"username\":\"alice\",\"is_bot\":false,\"delete_at\":0}";
        await using var server = new HttpEndpoint(body);
        await using var proxy = new HttpEndpoint("proxy", HttpStatusCode.BadGateway);
        using var handler = MattermostUsernameResolver.CreateHandler(bypassProxy: true, proxy: new FixedProxy(proxy.Uri));
        using var resolver = new MattermostUsernameResolver(handler);

        var user = await resolver.ResolveAsync(server.Uri.AbsoluteUri, "token", "alice");

        Assert.Equal("alice", user.Username);
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(0, proxy.RequestCount);
    }

    [Fact]
    public async Task Mattermost_thread_lookup_uses_proxy_when_bypass_is_off()
    {
        const string root = "0123456789abcdefghijklmnop";
        const string channel = "3123456789abcdefghijklmnop";
        await using var server = new HttpEndpoint($"{{\"id\":\"{root}\",\"channel_id\":\"{channel}\",\"root_id\":\"\",\"delete_at\":0}}");
        await using var proxy = new HttpEndpoint("proxy", HttpStatusCode.BadGateway);
        using var handler = MattermostUsernameResolver.CreateHandler(proxy: new FixedProxy(proxy.Uri));
        using var resolver = new MattermostThreadResolver(handler);

        var error = await Assert.ThrowsAsync<MattermostThreadLookupException>(() =>
            resolver.ResolveAsync(server.Uri.AbsoluteUri, "token", channel, root));

        Assert.Equal(MattermostThreadLookupFailure.Network, error.Failure);
        Assert.Equal(1, proxy.RequestCount);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task WebSocket_options_route_through_proxy_by_default_and_directly_when_bypassing()
    {
        await using var server = new WebSocketEndpoint();
        await using var proxy = new RejectingProxyEndpoint();
        var rejectingProxy = new FixedProxy(proxy.Uri);

        using (var proxied = new ClientWebSocket())
        {
            TransportHttp.ConfigureMattermostWebSocketProxy(proxied.Options, bypassProxy: false, rejectingProxy);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => proxied.ConnectAsync(server.Uri, CancellationToken.None));
            Assert.True(proxy.RequestCount == 1, failure.ToString());
        }
        Assert.Equal(0, server.ConnectionCount);

        using (var direct = new ClientWebSocket())
        {
            TransportHttp.ConfigureMattermostWebSocketProxy(direct.Options, bypassProxy: true, rejectingProxy);
            await direct.ConnectAsync(server.Uri, CancellationToken.None);
            await direct.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
        }
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(1, proxy.RequestCount);
    }

    private sealed class FixedProxy(Uri uri) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => uri;
        public bool IsBypassed(Uri host) => false;
    }

    private sealed class HttpEndpoint : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serve;
        private int _requestCount;

        public HttpEndpoint(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var port = FreePort();
            Uri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Uri.AbsoluteUri);
            _listener.Start();
            _serve = ServeAsync(body, status);
        }

        public Uri Uri { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync(string body, HttpStatusCode status)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                Interlocked.Increment(ref _requestCount);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try { await _serve; } catch (HttpListenerException) { }
        }
    }

    private sealed class WebSocketEndpoint : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serve;
        private int _connectionCount;

        public WebSocketEndpoint()
        {
            var port = FreePort();
            Uri = new Uri($"ws://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serve = ServeAsync();
        }

        public Uri Uri { get; }
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        private async Task ServeAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                Interlocked.Increment(ref _connectionCount);
                var buffer = new byte[128];
                while (socket.State == WebSocketState.Open)
                {
                    var message = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                }
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            catch (WebSocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try { await _serve; } catch (HttpListenerException) { }
        }
    }

    private sealed class RejectingProxyEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        private int _requestCount;

        public RejectingProxyEndpoint()
        {
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serve = ServeAsync();
        }

        public Uri Uri { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _requestCount);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer);
                Assert.True(read > 0);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _serve; } catch (SocketException) { }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
