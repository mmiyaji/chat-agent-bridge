using System.Text.Json.Nodes;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AgentProfile.Gemini, "gemini", AgentProfile.DefaultGeminiArguments)]
    [InlineData(AgentProfile.GitHubCopilot, "copilot", AgentProfile.DefaultGitHubCopilotArguments)]
    public async Task SessionCliModelNamesPersistWithoutCatalogValidation(string provider, string executable, string arguments)
    {
        var settings = new BridgeSettings
        {
            AgentProvider = provider, AgentExecutable = executable, AgentArguments = arguments,
            AgentModel = "  future-model-alias  ", AgentPermissionMode = "inherit"
        };
        var store = new SettingsStore(_directory);

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync();

        Assert.Equal(settings with { AgentModel = "future-model-alias" }, restored);
        await store.SaveAsync(restored with { AgentModel = " " });
        Assert.Null((await store.LoadAsync()).AgentModel);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini, "AgentReasoningEffort", "high")]
    [InlineData(AgentProfile.GitHubCopilot, "AgentReasoningEffort", "high")]
    [InlineData(AgentProfile.Gemini, "AgentPermissionMode", "workspace-write")]
    [InlineData(AgentProfile.GitHubCopilot, "AgentPermissionMode", "full-access")]
    public async Task SessionCliManagedOverridesAreRejectedOnSaveAndLoad(string provider, string property, string value)
    {
        var settings = new BridgeSettings { AgentProvider = provider, AgentModel = "model-alias" };
        var store = new SettingsStore(_directory);
        var invalid = property == "AgentReasoningEffort"
            ? settings with { AgentReasoningEffort = value }
            : settings with { AgentPermissionMode = value };
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));

        await store.SaveAsync(settings);
        var path = Path.Combine(_directory, "settings.json");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored[property] = value;
        await File.WriteAllTextAsync(path, stored.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
