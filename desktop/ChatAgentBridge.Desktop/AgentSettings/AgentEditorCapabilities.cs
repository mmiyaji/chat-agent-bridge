using ChatAgentBridge.Core;

namespace ChatAgentBridge_Desktop.AgentSettings;

/// <summary>One agent editor field that settings search can open: its label resource, control and extra search words.</summary>
public sealed record AgentEditorField(string LabelResource, string AutomationId, string Keywords);

/// <summary>
/// Which agent editor fields each CLI shows. The editor's view model and the settings search catalog both read
/// these rules, so search never offers a field the editor hides for that CLI (or misses one it shows).
/// </summary>
public static class AgentEditorCapabilities
{
    public static bool IsBasic(string provider) => AgentProfile.IsBasicCliProvider(provider) || provider == AgentProfile.Custom;
    public static bool IsArgumentsEditable(string provider) => IsBasic(provider);
    public static bool SupportsModel(string provider) => provider is AgentProfile.Codex or AgentProfile.Claude or
        AgentProfile.Gemini or AgentProfile.GitHubCopilot or AgentProfile.Antigravity;
    public static bool SupportsReasoning(string provider) => provider is AgentProfile.Codex or AgentProfile.Claude;
    // Antigravity lists models with `agy models` on request, but has no per-model effort levels.
    public static bool SupportsModelCatalog(string provider) => SupportsReasoning(provider) || provider == AgentProfile.Antigravity;
    // Antigravity maps workspace-write and full-access onto its own flags; the other basic CLIs manage permissions themselves.
    public static bool CanConfigurePermission(string provider) => !IsBasic(provider) || provider == AgentProfile.Antigravity;
    // App Server mode and the tool network switch are Codex options.
    public static bool HasCodexOptions(string provider) => provider == AgentProfile.Codex;

    public static IEnumerable<AgentEditorField> SearchableFields(string provider)
    {
        yield return new("AgentEditorExecutableLabel.Text", "AgentExecutable", "executable CLI path 実行ファイル パス");
        if (SupportsModel(provider)) yield return new("AgentEditorModelLabel.Text", "AgentModel", "model models モデル");
        if (SupportsReasoning(provider)) yield return new("AgentEditorReasoningLabel.Text", "ReasoningEffortCandidates", "reasoning effort 推論 強さ");
        if (CanConfigurePermission(provider)) yield return new("AgentEditorPermissionLabel.Text", "AgentPermissionMode", "permissions access 権限 許可");
        if (IsArgumentsEditable(provider)) yield return new("AgentEditorArgumentsLabel.Text", "AgentArguments", "arguments 引数");
        if (HasCodexOptions(provider))
        {
            yield return new("UseAppServer.Header", "UseAppServer", "app server 承認 質問");
            yield return new("AllowAgentNetwork.Header", "AllowAgentNetwork", "network ネットワーク");
        }
    }
}
