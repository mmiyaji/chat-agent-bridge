using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    // The overview describes committed settings, never drafts in an agent editor.
    public string SavedAgentName => _settings?.AgentProvider switch
    {
        "codex" => "Codex", "claude" => "Claude Code", "gemini" => "Gemini CLI",
        "github-copilot" => "GitHub Copilot", "antigravity" => "Antigravity CLI", "custom" => DesktopText.Get("GeneralSavedOtherCli"),
        _ => ViewModelText.Get("NotLoaded")
    };
    public string SavedModel => string.IsNullOrWhiteSpace(_settings?.AgentModel) || _settings.AgentModel == "default"
        ? ViewModelText.Get("DefaultCliValue") : _settings.AgentModel;
    public string SavedReasoning => string.IsNullOrWhiteSpace(_settings?.AgentReasoningEffort)
        ? ViewModelText.Get("DefaultCliValue") : _settings.AgentReasoningEffort;
    public string SavedExecutable => _settings?.AgentExecutable ?? ViewModelText.Get("NotLoaded");
    public string SavedWorkspace => _settings?.WorkspaceRoot ?? ViewModelText.Get("NotLoaded");

    private void RefreshSavedOverview()
    {
        foreach (var property in new[] { nameof(SavedAgentName), nameof(SavedModel), nameof(SavedReasoning),
                     nameof(SavedExecutable), nameof(SavedWorkspace) }) OnPropertyChanged(property);
    }
}
