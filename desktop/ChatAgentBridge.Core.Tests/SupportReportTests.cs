using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Diagnostics;
using System.Text.Json;

public sealed class SupportReportTests
{
    [Fact]
    public void JsonContainsOnlyAllowlistedSupportFieldsAndStatuses()
    {
        var report = SupportReport.Create("1.2.3.4-1.2.3+abcdef", "10.0.22631", "10.0.1", "X64", "Codex", "2.1.0",
            new[] { "cli_verified", "notifications_registered", "D:\\private\\workspace", "chat_not_configured" },
            "diagnostic_unavailable", "0x80070005");
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal(new[] { "Architecture", "Build", "CliVersion", "ErrorType", "HResult", "Product", "Provider", "RuntimeVersion", "SettingsNavigation", "Status", "StorageOperations", "WindowsVersion" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Chat Agent Bridge", root.GetProperty("Product").GetString());
        Assert.Equal(new[] { "chat_not_configured", "cli_verified", "notifications_registered" },
            root.GetProperty("Status").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("workspace", report.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("D:\\private\\workspace")]
    [InlineData("user@example.com")]
    [InlineData("Bearer private-token")]
    [InlineData("xoxb-FAKE-SECRET")]
    [InlineData("prompt secret in path D:\\private")]
    public void UnsafeBuildAndCliValuesAreDiscarded(string unsafeValue)
    {
        var report = SupportReport.Create(unsafeValue, "10.0.22631", "10.0.1", "X64", "Codex", unsafeValue, Array.Empty<string>());
        Assert.Equal("unknown", report.Build);
        Assert.Equal("unknown", report.CliVersion);
    }

    [Fact]
    public void CliVersionParsingKeepsOnlyTheNumericVersionPrefix()
    {
        var report = SupportReport.Create("1.2.3+trusted-build", "10.0.22631", "10.0.1", "X64", "Codex",
            "2.1.0-xoxb-FAKE-SECRET", Array.Empty<string>());
        Assert.Equal("2.1.0", report.CliVersion);
        Assert.DoesNotContain("xoxb", report.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemVersionsRequireNumericDottedValues()
    {
        var report = SupportReport.Create("1.2.3", "10.0.1-path-secret", "Bearer token", "X64", "Codex", "2.0.0", Array.Empty<string>());
        Assert.Equal("unknown", report.WindowsVersion);
        Assert.Equal("unknown", report.RuntimeVersion);
    }

    [Fact]
    public void UnknownSystemArchitectureAndProviderAreReducedToSafeValues()
    {
        var report = SupportReport.Create("1.2.3", "10.0.1", "10.0.1", "D:\\user", "xoxb-token", "2.0.0", Array.Empty<string>());
        Assert.Equal("Unknown", report.Architecture);
        Assert.Equal("Unknown", report.Provider);
    }

    [Fact]
    public void StorageDiagnosticsAreBoundedAndCannotExportPathsOrRawErrors()
    {
        var safe = new StorageOperationDiagnostic(DateTimeOffset.UtcNow, "read", 100, 2, "success", null);
        var records = new[]
        {
            safe with { Operation = @"D:\private\secret" },
            safe with { Outcome = "Bearer private-token" },
            safe with { RetryCount = -1 },
            safe with { ElapsedMilliseconds = -1 }
        }.Concat(Enumerable.Repeat(safe, 100));
        var report = SupportReport.Create("1.2.3", "10.0.1", "10.0.1", "X64", "Codex", "2.0.0", [], storageOperations: records);
        Assert.InRange(report.StorageOperations.Count, 1, 64);
        Assert.All(report.StorageOperations, item => Assert.Equal("read", item.Operation));
        Assert.DoesNotContain("private", report.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", report.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NavigationDiagnosticsDoNotAcceptUserLabelsAndAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsNavigationDiagnostics.Record("private-path", 1, true));
        for (var i = 0; i < 40; i++) SettingsNavigationDiagnostics.Record("agent_settings", i, true);
        Assert.InRange(SettingsNavigationDiagnostics.Snapshot().Count, 1, 32);
        var unsafeEntry = new SettingsNavigationDiagnostic(DateTimeOffset.UtcNow, "Bearer private-token", 10, false);
        var report = SupportReport.Create("1.2.3", "10.0.1", "10.0.1", "X64", "Codex", "2.0.0", [],
            settingsNavigation: new[] { unsafeEntry }.Concat(SettingsNavigationDiagnostics.Snapshot()));
        Assert.All(report.SettingsNavigation, item => Assert.Equal("agent_settings", item.Page));
        Assert.DoesNotContain("private", report.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
