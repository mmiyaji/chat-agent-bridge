namespace ChatAgentBridge.Core.Tests;

public sealed class OnboardingFieldChecksTests
{
    [Theory]
    [InlineData(false, false, new[] { WizardField.Terms, WizardField.Privacy })]
    [InlineData(true, false, new[] { WizardField.Privacy })]
    [InlineData(false, true, new[] { WizardField.Terms })]
    [InlineData(true, true, new WizardField[0])]
    public void ConsentListsOnlyUncheckedBoxes(bool terms, bool privacy, WizardField[] expected) =>
        Assert.Equal(expected, OnboardingFieldChecks.Consent(terms, privacy));

    [Fact]
    public void AgentStepListsFieldsInScreenOrder()
    {
        Assert.Equal([WizardField.WorkspaceRoot, WizardField.AgentProvider, WizardField.AgentExecutable],
            OnboardingFieldChecks.AgentStep(false, " ", false));
        Assert.Equal([WizardField.AgentExecutable], OnboardingFieldChecks.AgentStep(true, "", true));
        Assert.Empty(OnboardingFieldChecks.AgentStep(true, "codex", true));
    }

    [Fact]
    public void CliStepNeedsACurrentCheck()
    {
        Assert.Equal([WizardField.CliCheck], OnboardingFieldChecks.CliStep(false));
        Assert.Empty(OnboardingFieldChecks.CliStep(true));
    }

    [Fact]
    public void SlackChatListsEveryMissingCredential()
    {
        Assert.Equal([WizardField.SlackBotToken, WizardField.SlackAppToken, WizardField.SlackAllowedUsers],
            OnboardingFieldChecks.SlackChat(null, "", false));
        Assert.Equal([WizardField.SlackAllowedUsers], OnboardingFieldChecks.SlackChat("xoxb", "xapp", false));
        Assert.Empty(OnboardingFieldChecks.SlackChat("xoxb", "xapp", true));
    }

    [Fact]
    public void MattermostChatListsEveryMissingValue()
    {
        Assert.Equal([WizardField.MattermostServerUrl, WizardField.MattermostChannel,
            WizardField.MattermostToken, WizardField.MattermostAllowedUsers],
            OnboardingFieldChecks.MattermostChat(false, " ", null, false));
        Assert.Equal([WizardField.MattermostChannel], OnboardingFieldChecks.MattermostChat(true, "", "t", true));
        Assert.Empty(OnboardingFieldChecks.MattermostChat(true, "c", "t", true));
    }

    [Fact]
    public void DiscordChatRequiresTokenAndUsersAndGuildOrDirectMessageOptIn()
    {
        Assert.Equal([WizardField.DiscordBotToken, WizardField.DiscordAllowedUsers, WizardField.DiscordAllowedGuilds],
            OnboardingFieldChecks.DiscordChat(null, false, false, false));
        Assert.Equal([WizardField.DiscordAllowedGuilds],
            OnboardingFieldChecks.DiscordChat("token", true, false, false));
        Assert.Empty(OnboardingFieldChecks.DiscordChat("token", true, false, true));
        Assert.Empty(OnboardingFieldChecks.DiscordChat("token", true, true, false));
    }
}
