using System.Net.Http;

namespace ChatAgentBridge.Transports;

internal static class DiscordHttp
{
    internal static string UserAgent { get; } =
        $"DiscordBot (https://github.com/mmiyaji/chat-agent-bridge, {typeof(DiscordHttp).Assembly.GetName().Version?.ToString() ?? "1.0.0"})";

    internal static void ConfigureRequest(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
}
