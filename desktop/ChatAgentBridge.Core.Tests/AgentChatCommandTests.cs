using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentChatCommandTests
{
    [Theory]
    [InlineData("agent", null, null)]
    [InlineData("!cab agent claude", "claude", null)]
    [InlineData("AGENT CODEX SUMMARY", "codex", true)]
    [InlineData("agent claude fresh", "claude", false)]
    public void ParsesSupportedAgentSwitchCommands(string text, string? provider, bool? includeSummary)
    {
        Assert.True(AgentChatCommand.TryParse(text, out var command));
        Assert.True(command.IsValid);
        Assert.Equal(provider, command.Provider);
        Assert.Equal(includeSummary, command.IncludeSummary);
    }

    [Theory]
    [InlineData("agent codex.exe", false)]
    [InlineData("agent C:\\Tools\\codex", false)]
    [InlineData("agent gemini", false)]
    [InlineData("agent claude summary extra", false)]
    [InlineData("agent claude resume", false)]
    public void ReservesUnsupportedAgentSwitchesAsInvalid(string text, bool expectedValid)
    {
        Assert.True(AgentChatCommand.TryParse(text, out var command));
        Assert.Equal(expectedValid, command.IsValid);
    }

    [Theory]
    [InlineData("please agent claude")]
    [InlineData("switch to claude")]
    [InlineData("run this task")]
    public void DoesNotConsumeOrdinaryChatAsAnAgentCommand(string text)
    {
        Assert.False(AgentChatCommand.TryParse(text, out _));
    }
}
