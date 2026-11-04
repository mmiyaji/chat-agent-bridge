namespace ChatAgentBridge.Core;

/// <summary>Safe startup maintenance for Muse's private prompt store.</summary>
public static class MusePromptRecovery
{
    /// <summary>Removes abandoned prompt files from terminated runs; active leases are preserved.</summary>
    public static void CleanupAbandoned() => MuseExecRunner.CleanupAbandonedPrompts();
}
