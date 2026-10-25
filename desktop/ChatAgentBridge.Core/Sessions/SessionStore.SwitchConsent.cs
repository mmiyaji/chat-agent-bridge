namespace ChatAgentBridge.Core.Sessions;

public sealed partial class SessionStore
{
    /// <summary>Rechecks exact conversation consent, including a validated folder or agent transition.</summary>
    public async Task<bool> RequiresApprovalAsync(SessionKey key, BridgeSettings effective, SessionBinding? binding,
        bool switchCommand = false, CancellationToken token = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var document = await ReadSharedAsync(token).ConfigureAwait(false);
            // A stale caller may never authorize execution after a conversation mutation.
            if (ExecutionBinding(document, key) != binding) return true;
            if (switchCommand) return !HasSwitchConsent(document, key, effective, binding);
            if (effective.ChatApprovalMode == "always") return true;
            if (HasTransferredConsent(document, key, effective)) return false;
            return ConversationExecutionPolicy.RequiresApproval(key, effective, binding) ||
                document.Agents?.Any(item => item.Key == key && item.RequiresApproval) == true;
        }
        finally { _gate.Release(); }
    }

    private static SessionBinding? ExecutionBinding(StoredSessions document, SessionKey key) =>
        document.Agents?.Any(item => item.Key == key && item.StartNewSession) == true
            ? null : document.Bindings!.FirstOrDefault(item => item.Key == key);

    private static bool HasSwitchConsent(StoredSessions document, SessionKey key, BridgeSettings effective,
        SessionBinding? binding) =>
        key.UserId is not null && key.Origin is not null &&
        ConversationExecutionPolicy.AutoApprovesSwitchCommands(effective) &&
        (!ConversationExecutionPolicy.RequiresApproval(key, effective, binding) || HasTransferredConsent(document, key, effective));

    private static bool HasTransferredConsent(StoredSessions document, SessionKey key, BridgeSettings effective) =>
        effective.AutoApproveSwitchCommands && effective.ChatApprovalMode is "first" or "auto" &&
        document.SwitchConsents?.Any(item => item.Key == key &&
            string.Equals(item.Fingerprint, ConversationExecutionPolicy.GetFingerprint(key, effective), StringComparison.OrdinalIgnoreCase)) == true;

    private static void SetSwitchConsent(StoredSessions document, SessionKey key, BridgeSettings? target)
    {
        document.SwitchConsents ??= [];
        document.SwitchConsents.RemoveAll(item => item.Key == key);
        if (target is { AutoApproveSwitchCommands: true, ChatApprovalMode: "first" or "auto" })
            document.SwitchConsents.Add(new(key, ConversationExecutionPolicy.GetFingerprint(key, target), DateTimeOffset.UtcNow));
        // Folder changes remove the old binding, so also bound pending consent independently of bindings.
        document.SwitchConsents = document.SwitchConsents.OrderBy(item => item.UpdatedAt).TakeLast(MaxBindings).ToList();
    }

    private static void ValidateSwitchConsents(StoredSessions document)
    {
        var keys = new HashSet<SessionKey>();
        foreach (var consent in document.SwitchConsents ?? [])
        {
            ValidateKey(consent.Key);
            if (consent.Key.UserId is null || consent.Key.Origin is null || !keys.Add(consent.Key) ||
                consent.UpdatedAt == default || consent.Fingerprint is null || consent.Fingerprint.Length != 64 || consent.Fingerprint.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid conversation switch consent.");
        }
    }

    private sealed record SwitchConsent(SessionKey Key, string Fingerprint, DateTimeOffset UpdatedAt);
}
