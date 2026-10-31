namespace ChatAgentBridge.Core;

/// <summary>How much a chat progress post shows while a request runs and after it finishes.</summary>
public static class ChatProgressDetail
{
    public const string Minimal = "minimal";
    public const string Standard = "standard";
    public const string Detailed = "detailed";

    public static string Normalize(string? value) => value switch
    {
        Minimal => Minimal,
        Detailed => Detailed,
        _ => Standard
    };
}
