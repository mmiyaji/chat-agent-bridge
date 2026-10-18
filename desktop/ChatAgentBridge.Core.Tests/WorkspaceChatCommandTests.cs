using Xunit;

namespace ChatAgentBridge.Core.Tests;

public sealed class WorkspaceChatCommandTests
{
    [Theory]
    [InlineData("folder C:\\Projects\\app", "C:\\Projects\\app")]
    [InlineData("/folder \"C:\\My Projects\\app\"\ncomment", "C:\\My Projects\\app")]
    [InlineData("folder", "")]
    public void ParsesOnlyTheConfigurationLine(string text, string expected)
    {
        Assert.True(WorkspaceChatCommand.TryParse(text, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("foldering instructions")]
    [InlineData("Please change folder")]
    public void DoesNotCaptureOrdinaryPrompts(string text) => Assert.False(WorkspaceChatCommand.TryParse(text, out _));
}
