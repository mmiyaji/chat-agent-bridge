namespace ChatAgentBridge.Core.Diagnostics;

public sealed record SettingsNavigationDiagnostic(DateTimeOffset RecordedAtUtc, string Page, long ElapsedMilliseconds, bool Succeeded);

/// <summary>Bounded process-local timings; no labels, field values or workspace paths.</summary>
public static class SettingsNavigationDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Queue<SettingsNavigationDiagnostic> Records = new();

    public static void Record(string page, long elapsedMilliseconds, bool succeeded)
    {
        if (page is not ("agent_settings" or "chat_settings") || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(page));
        lock (Gate)
        {
            if (Records.Count == 32) Records.Dequeue();
            Records.Enqueue(new(DateTimeOffset.UtcNow, page, elapsedMilliseconds, succeeded));
        }
    }

    public static IReadOnlyList<SettingsNavigationDiagnostic> Snapshot()
    {
        lock (Gate) return Records.ToArray();
    }
}
