namespace ChatAgentBridge.Core;

/// <summary>Required wizard inputs that can be highlighted when the user tries to continue without them.</summary>
public enum WizardField
{
    Terms,
    Privacy,
    AgentProvider,
    AgentExecutable,
    WorkspaceRoot,
    CliCheck,
    SlackBotToken,
    SlackAppToken,
    SlackAllowedUsers,
    MattermostServerUrl,
    MattermostToken,
    MattermostAllowedUsers,
    DiscordBotToken,
    DiscordAllowedUsers,
}

/// <summary>Lists the missing required inputs of a wizard step, in the order they appear on screen.</summary>
public static class OnboardingFieldChecks
{
    public static IReadOnlyList<WizardField> Consent(bool acceptedTerms, bool acceptedPrivacy)
    {
        var missing = new List<WizardField>();
        if (!acceptedTerms) missing.Add(WizardField.Terms);
        if (!acceptedPrivacy) missing.Add(WizardField.Privacy);
        return missing;
    }

    public static IReadOnlyList<WizardField> AgentStep(bool providerSelected, string? executable, bool workspaceUsable)
    {
        var missing = new List<WizardField>();
        if (!workspaceUsable) missing.Add(WizardField.WorkspaceRoot);
        if (!providerSelected) missing.Add(WizardField.AgentProvider);
        if (string.IsNullOrWhiteSpace(executable)) missing.Add(WizardField.AgentExecutable);
        return missing;
    }

    public static IReadOnlyList<WizardField> CliStep(bool hasCurrentCliCheck) =>
        hasCurrentCliCheck ? [] : [WizardField.CliCheck];

    public static IReadOnlyList<WizardField> SlackChat(string? botToken, string? appToken, bool allowedUsersValid)
    {
        var missing = new List<WizardField>();
        if (string.IsNullOrWhiteSpace(botToken)) missing.Add(WizardField.SlackBotToken);
        if (string.IsNullOrWhiteSpace(appToken)) missing.Add(WizardField.SlackAppToken);
        if (!allowedUsersValid) missing.Add(WizardField.SlackAllowedUsers);
        return missing;
    }

    public static IReadOnlyList<WizardField> MattermostChat(bool serverUrlValid, string? token, bool allowedUsersValid)
    {
        var missing = new List<WizardField>();
        if (!serverUrlValid) missing.Add(WizardField.MattermostServerUrl);
        if (string.IsNullOrWhiteSpace(token)) missing.Add(WizardField.MattermostToken);
        if (!allowedUsersValid) missing.Add(WizardField.MattermostAllowedUsers);
        return missing;
    }

    public static IReadOnlyList<WizardField> DiscordChat(string? botToken, bool allowedUsersValid)
    {
        var missing = new List<WizardField>();
        if (string.IsNullOrWhiteSpace(botToken)) missing.Add(WizardField.DiscordBotToken);
        if (!allowedUsersValid) missing.Add(WizardField.DiscordAllowedUsers);
        return missing;
    }
}
