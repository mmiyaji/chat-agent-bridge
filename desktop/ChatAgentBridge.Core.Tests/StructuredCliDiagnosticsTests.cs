using System.Globalization;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core.Tests;

public sealed class StructuredCliDiagnosticsTests
{
    [Theory]
    [InlineData("IneligibleTierError", "CliAccountUnsupported")]
    [InlineData("UNSUPPORTED_CLIENT", "CliAccountUnsupported")]
    [InlineData("UNAUTHENTICATED", "CliAuthenticationRequired")]
    [InlineData("Authentication failed", "CliAuthenticationRequired")]
    [InlineData("Invalid credentials", "CliAuthenticationRequired")]
    [InlineData("Could not load credentials", "CliAuthenticationRequired")]
    [InlineData("Please set an Auth method in your private/settings GEMINI_API_KEY=secret", "CliAuthenticationRequired")]
    [InlineData("Untrusted directory", "CliWorkspaceUntrusted")]
    [InlineData("The workspace is not trusted", "CliWorkspaceUntrusted")]
    [InlineData("Gemini CLI is not running in a trusted directory. To proceed, either use --skip-trust", "CliWorkspaceUntrusted")]
    [InlineData("Error: Model \"private-model\" from --model flag is not available.", "CliModelUnavailable")]
    [InlineData("Model private-model not found", "CliModelUnavailable")]
    public void Known_failures_return_only_localized_fixed_text(string diagnostic, string resource)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "en", "ja" })
            foreach (var provider in new[] { AgentProfile.Gemini, AgentProfile.GitHubCopilot })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var untrusted = diagnostic + "\nhttps://private.example/login?token=secret-token C:\\private\\folder prompt-private tool-private";
                var error = StructuredCliDiagnostics.Error(provider, untrusted, 55, hasValidResult: false);
                Assert.Equal(CoreText.Get(resource), error);
                Assert.NotEqual(resource, error);
                Assert.DoesNotContain("private", error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret-token", error);
                Assert.DoesNotContain(CoreText.Get("CliStructuredResultInvalid"), error);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("UNSUPPORTED_CLIENT\nUNAUTHENTICATED\nuntrusted directory\nmodel not available", "CliAccountUnsupported")]
    [InlineData("UNAUTHENTICATED\nuntrusted directory\nmodel not available", "CliAuthenticationRequired")]
    [InlineData("untrusted directory\nmodel not available", "CliWorkspaceUntrusted")]
    public void Primary_blocker_takes_precedence(string stderr, string resource)
        => Assert.Equal(CoreText.Get(resource), StructuredCliDiagnostics.Error(AgentProfile.Gemini, stderr, 1, false));

    [Fact]
    public void Unknown_diagnostics_never_escape_and_keep_protocol_guidance()
    {
        var error = StructuredCliDiagnostics.Error(AgentProfile.GitHubCopilot,
            "https://private.example/token=secret-token C:\\private\\prompt tool-payload", 1, false);
        Assert.Equal(CoreText.Get("CliDiagnosticsHidden") + Environment.NewLine + CoreText.Get("CliStructuredResultInvalid"), error);
    }

    [Fact]
    public void Successful_exit_without_terminal_result_still_reports_protocol_failure()
    {
        var error = StructuredCliDiagnostics.Error(AgentProfile.Gemini, "UNAUTHENTICATED private-prompt", 0, false);
        Assert.Equal(CoreText.Get("CliAuthenticationRequired") + Environment.NewLine + CoreText.Get("CliStructuredResultInvalid"), error);
    }

    [Fact]
    public void Valid_terminal_result_needs_no_format_warning()
    {
        Assert.Equal("", StructuredCliDiagnostics.Error(AgentProfile.Gemini, "", 0, true));
        Assert.Equal(CoreText.Get("CliDiagnosticsHidden"), StructuredCliDiagnostics.Error(AgentProfile.GitHubCopilot, "private-data", 0, true));
        Assert.Equal(CoreText.Get("CliStructuredResultInvalid"), StructuredCliDiagnostics.Error(AgentProfile.Gemini, "", 1, false));
    }

    [Fact]
    public void Classification_only_reads_bounded_prefix()
    {
        var prefix = new string('x', StructuredCliDiagnostics.MaxDiagnosticCharacters);
        var error = StructuredCliDiagnostics.Error(AgentProfile.Gemini, prefix + "UNAUTHENTICATED", 1, false);
        Assert.Equal(CoreText.Get("CliDiagnosticsHidden") + Environment.NewLine + CoreText.Get("CliStructuredResultInvalid"), error);
        Assert.Equal(CoreText.Get("CliAuthenticationRequired"), StructuredCliDiagnostics.Error(AgentProfile.Gemini, "UNAUTHENTICATED" + prefix, 1, false));
    }

    [Fact]
    public void Other_providers_cannot_use_basic_cli_diagnostic_policy()
        => Assert.Throws<ArgumentException>(() => StructuredCliDiagnostics.Error(AgentProfile.Claude, "private", 1, false));
}
