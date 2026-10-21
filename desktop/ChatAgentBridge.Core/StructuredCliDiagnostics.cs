using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

/// <summary>Classifies bounded CLI diagnostics without returning any untrusted diagnostic text.</summary>
internal static class StructuredCliDiagnostics
{
    internal const int MaxDiagnosticCharacters = 32_768;

    internal static string Error(string provider, string stderr, int exitCode, bool hasValidResult)
    {
        if (!AgentProfile.IsBasicCliProvider(provider))
            throw new ArgumentException("Structured CLI diagnostics require Gemini or GitHub Copilot.", nameof(provider));
        var diagnostic = stderr.AsSpan(0, Math.Min(stderr.Length, MaxDiagnosticCharacters));
        // Startup messages can contain several failures. Prefer the account blocker over
        // secondary workspace warnings; no matched value is ever inserted into the response.
        string? key = null;
        if (Has(diagnostic, "UNSUPPORTED_CLIENT") || Has(diagnostic, "IneligibleTierError"))
            key = "CliAccountUnsupported";
        else if (Has(diagnostic, "UNAUTHENTICATED") || Has(diagnostic, "authentication failed") ||
                 Has(diagnostic, "not authenticated") || Has(diagnostic, "not logged in") ||
                 Has(diagnostic, "login required") || Has(diagnostic, "sign in required") ||
                 Has(diagnostic, "please set an auth method") ||
                 Has(diagnostic, "invalid credentials") || Has(diagnostic, "no credentials") ||
                 Has(diagnostic, "credentials not found") || Has(diagnostic, "could not load credentials") ||
                 Has(diagnostic, "credential error"))
            key = "CliAuthenticationRequired";
        else if (Has(diagnostic, "not running in a trusted directory") ||
                 Has(diagnostic, "untrusted directory") || Has(diagnostic, "untrusted workspace") ||
                 Has(diagnostic, "directory is not trusted") || Has(diagnostic, "workspace is not trusted") ||
                 Has(diagnostic, "folder is not trusted"))
            key = "CliWorkspaceUntrusted";
        else if (Has(diagnostic, "model") && (Has(diagnostic, "not available") ||
                 Has(diagnostic, "not found") || Has(diagnostic, "unavailable")))
            key = "CliModelUnavailable";

        var message = string.IsNullOrWhiteSpace(stderr) ? "" : CoreText.Get(key ?? "CliDiagnosticsHidden");
        // A known startup failure explains an absent result. An otherwise successful
        // process must still prove the protocol, even if stderr contains a known warning.
        if (!hasValidResult && (exitCode == 0 || key is null))
            message = string.IsNullOrWhiteSpace(message) ? CoreText.Get("CliStructuredResultInvalid")
                : message + Environment.NewLine + CoreText.Get("CliStructuredResultInvalid");
        return message;
    }

    private static bool Has(ReadOnlySpan<char> diagnostic, string value)
        => diagnostic.Contains(value.AsSpan(), StringComparison.OrdinalIgnoreCase);
}
