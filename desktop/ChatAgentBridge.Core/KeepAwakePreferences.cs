namespace ChatAgentBridge.Core;

public sealed record KeepAwakePreferences
{
    public string Mode { get; init; } = KeepAwakeMode.Requests;
    public bool AcOnly { get; init; } = true;
    public KeepAwakePreferences Validate() => this with { Mode = KeepAwakeMode.Normalize(Mode) };
}

public static class KeepAwakeMode
{
    public const string Off = "off";
    public const string Requests = "requests";
    public const string Receiver = "receiver";
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Requests => Requests, Receiver => Receiver, _ => Off
    };
}

public static class KeepAwakePolicy
{
    public static bool ShouldStayAwake(KeepAwakePreferences preferences, bool receiverIntended,
        bool requestActive, bool manualOperationActive, bool onAcPower)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var mode = KeepAwakeMode.Normalize(preferences.Mode);
        if (mode == KeepAwakeMode.Off || (preferences.AcOnly && !onAcPower)) return false;
        return mode switch
        {
            KeepAwakeMode.Requests => requestActive || manualOperationActive,
            KeepAwakeMode.Receiver => receiverIntended || requestActive || manualOperationActive,
            _ => false
        };
    }
}
