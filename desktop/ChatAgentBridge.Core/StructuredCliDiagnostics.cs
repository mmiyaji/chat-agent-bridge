using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

/// <summary>Classifies bounded CLI diagnostics without returning any untrusted diagnostic text.</summary>
internal static class StructuredCliDiagnostics
{
    internal const int MaxDiagnosticCharacters = 32_768;

    internal static string Error(string provider, string stderr, int exitCode, bool hasValidResult)
    {
        if (!AgentProfile.IsBasicCliProvider(provider))
            throw new ArgumentException("Structured CLI diagnostics require Gemini, GitHub Copilot, or Antigravity.", nameof(provider));
        var known = KnownError(stderr);
        var message = string.IsNullOrWhiteSpace(stderr) ? "" : known ?? CoreText.Get("CliDiagnosticsHidden");
        // A known startup failure explains an absent result. An otherwise successful
        // process must still prove the protocol, even if stderr contains a known warning.
        if (!hasValidResult && (exitCode == 0 || known is null))
            message = string.IsNullOrWhiteSpace(message) ? CoreText.Get("CliStructuredResultInvalid")
                : message + Environment.NewLine + CoreText.Get("CliStructuredResultInvalid");
        return message;
    }

    internal static string? KnownError(string text)
    {
        var diagnostic = text.AsSpan(0, Math.Min(text.Length, MaxDiagnosticCharacters));
        // Startup messages can contain several failures. Prefer the account blocker over
        // secondary workspace warnings; no matched value is ever inserted into the response.
        string? key = null;
        if (Has(diagnostic, "UNSUPPORTED_CLIENT") || Has(diagnostic, "IneligibleTierError") ||
            Has(diagnostic, "subscription does not have access") || Has(diagnostic, "API-Key restrictions") ||
            (Has(diagnostic, "403") && Has(diagnostic, "subscription")))
            key = "CliAccountUnsupported";
        else if (Has(diagnostic, "UNAUTHENTICATED") || Has(diagnostic, "authentication failed") ||
                 Has(diagnostic, "invalid API key") || (Has(diagnostic, "401") && Has(diagnostic, "API key")) ||
                 Has(diagnostic, "not authenticated") || Has(diagnostic, "not logged in") ||
                 Has(diagnostic, "login required") || Has(diagnostic, "sign in required") ||
                 Has(diagnostic, "please set an auth method") ||
                 Has(diagnostic, "invalid credentials") || Has(diagnostic, "no credentials") ||
                 Has(diagnostic, "credentials not found") || Has(diagnostic, "could not load credentials") ||
                 Has(diagnostic, "credential error") || Has(diagnostic, "KIRO_API_KEY"))
            key = "CliAuthenticationRequired";
        else if (Has(diagnostic, "FatalUntrustedWorkspaceError") || Has(diagnostic, "not inside a trusted directory") ||
                 Has(diagnostic, "not running in a trusted directory") ||
                 Has(diagnostic, "untrusted directory") || Has(diagnostic, "untrusted workspace") ||
                 Has(diagnostic, "directory is not trusted") || Has(diagnostic, "workspace is not trusted") ||
                 Has(diagnostic, "folder is not trusted"))
            key = "CliWorkspaceUntrusted";
        else if (Has(diagnostic, "model") && (Has(diagnostic, "not available") ||
                 Has(diagnostic, "not found") || Has(diagnostic, "unavailable")))
            key = "CliModelUnavailable";

        return key is null ? null : CoreText.Get(key);
    }

    private static bool Has(ReadOnlySpan<char> diagnostic, string value)
        => diagnostic.Contains(value.AsSpan(), StringComparison.OrdinalIgnoreCase);
}
