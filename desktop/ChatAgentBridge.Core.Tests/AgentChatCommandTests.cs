using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentChatCommandTests
{
    [Theory]
    [InlineData("!cab agent", null, null)]
    [InlineData("!cab agent claude", "claude", null)]
    [InlineData("!cab AGENT CODEX SUMMARY", "codex", true)]
    [InlineData("!cab agent claude fresh", "claude", false)]
    public void ParsesSupportedAgentSwitchCommands(string text, string? provider, bool? includeSummary)
    {
        Assert.True(AgentChatCommand.TryParse(text, out var command));
        Assert.True(command.IsValid);
        Assert.Equal(provider, command.Provider);
        Assert.Equal(includeSummary, command.IncludeSummary);
    }

    [Theory]
    [InlineData("!cab agent codex.exe", false)]
    [InlineData("!cab agent C:\\Tools\\codex", false)]
    [InlineData("!cab agent gemini fresh", false)]
    [InlineData("!cab agent claude summary extra", false)]
    [InlineData("!cab agent claude resume", false)]
    public void ReservesUnsupportedAgentSwitchesAsInvalid(string text, bool expectedValid)
    {
        Assert.True(AgentChatCommand.TryParse(text, out var command));
        Assert.Equal(expectedValid, command.IsValid);
    }

    [Theory]
    [InlineData("please agent claude")]
    [InlineData("agent")]
    [InlineData("agent loop through the logs and identify the failure")]
    [InlineData("agent claude")]
    [InlineData("!cab agent のログを調べて")]
    [InlineData("!cab agent loop を実装して")]
    [InlineData("!cab agent gemini")]
    [InlineData("switch to claude")]
    [InlineData("run this task")]
    [InlineData("!cab status")]
    public void DoesNotConsumeOrdinaryChatAsAnAgentCommand(string text)
    {
        Assert.False(AgentChatCommand.TryParse(text, out _));
    }
}
