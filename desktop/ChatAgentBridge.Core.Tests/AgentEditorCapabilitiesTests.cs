using System.Text.RegularExpressions;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.AgentSettings;
using ChatAgentBridge_Desktop.Search;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentEditorCapabilitiesTests
{
    private static readonly string[] Providers =
        [AgentProfile.Codex, AgentProfile.Claude, AgentProfile.Gemini, AgentProfile.GitHubCopilot, AgentProfile.Antigravity, AgentProfile.Custom];

    [Fact]
    public void ModelCatalogRuleMatchesAgentProfile()
    {
        foreach (var provider in Providers)
            Assert.Equal(AgentProfile.HasModelCatalog(provider), AgentEditorCapabilities.SupportsModelCatalog(provider));
    }

    [Fact]
    public void BasicProviderRuleMatchesAgentProfile()
    {
        foreach (var provider in Providers)
            Assert.Equal(AgentProfile.IsBasicCliProvider(provider) || provider == AgentProfile.Custom, AgentEditorCapabilities.IsBasic(provider));
    }

    [Theory]
    [InlineData("codex", "AgentExecutable,AgentModel,ReasoningEffortCandidates,AgentPermissionMode,UseAppServer,AllowAgentNetwork")]
    [InlineData("claude", "AgentExecutable,AgentModel,ReasoningEffortCandidates,AgentPermissionMode")]
    [InlineData("gemini", "AgentExecutable,AgentModel,AgentArguments")]
    [InlineData("github-copilot", "AgentExecutable,AgentModel,AgentArguments")]
    [InlineData("antigravity", "AgentExecutable,AgentModel,AgentPermissionMode,AgentArguments")]
    [InlineData("custom", "AgentExecutable,AgentArguments")]
    public void SearchableFieldsFollowWhatTheEditorShows(string provider, string expected)
        => Assert.Equal(expected.Split(','), AgentEditorCapabilities.SearchableFields(provider).Select(field => field.AutomationId));

    [Fact]
    public void EverySearchableFieldExistsInTheEditorXaml()
    {
        var xaml = File.ReadAllText(FindFile("ChatAgentBridge.Desktop", "AgentSettings", "AgentProfileEditorControl.xaml"));
        foreach (var field in Providers.SelectMany(AgentEditorCapabilities.SearchableFields).DistinctBy(field => field.AutomationId))
            Assert.Contains($"AutomationProperties.AutomationId=\"{field.AutomationId}\"", xaml);
    }

    [Theory]
    [InlineData("chatAgentBridgeDesktopMainPageTextBlock049", true)]
    [InlineData("onboardingOnboardingWizardTextBlock004", true)]
    [InlineData("AgentModel", false)]
    [InlineData("MattermostToken", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GeneratedIdsAreRecognized(string? id, bool generated) => Assert.Equal(generated, SettingsSearchMatcher.IsGeneratedId(id));

    private static string FindFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var root in new[] { directory.FullName, Path.Combine(directory.FullName, "desktop") })
            {
                var path = Path.Combine([root, .. parts]);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
