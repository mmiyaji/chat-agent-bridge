using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatControlCommandTests
{
    [Theory]
    [InlineData("status", ChatControlCommandKind.Status)]
    [InlineData("  STATUS  ", ChatControlCommandKind.Status)]
    [InlineData("!cab status", ChatControlCommandKind.Status)]
    [InlineData("stop", ChatControlCommandKind.Stop)]
    [InlineData("!cab stop", ChatControlCommandKind.Stop)]
    public void ParsesExactStatusAndStopCommands(string text, ChatControlCommandKind expected)
    {
        Assert.Equal(expected, ChatControlCommand.Parse(text).Kind);
    }

    [Fact]
    public void ParsesNumberedAnswerWithoutExposingQuestionId()
    {
        var parsed = ChatControlCommand.Parse("!cab answer 012345abcdef 2 Use option A\nwith details");

        Assert.Equal(ChatControlCommandKind.Answer, parsed.Kind);
        Assert.Equal("012345ABCDEF", parsed.Code);
        Assert.Equal(2, parsed.QuestionNumber);
        Assert.Equal("Use option A\nwith details", parsed.Answer);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("answer no-code 1 value")]
    [InlineData("answer 012345abcdef 0 value")]
    [InlineData("answer 012345abcdef 101 value")]
    [InlineData("answer 012345abcdef 1")]
    [InlineData("!cab answer 012345abcdef 1 ")]
    public void MalformedAnswerRemainsReserved(string text)
    {
        Assert.Equal(ChatControlCommandKind.ReservedInvalid, ChatControlCommand.Parse(text).Kind);
    }

    [Theory]
    [InlineData("please status")]
    [InlineData("stop the build")]
    [InlineData("!cab run this")]
    public void NonCommandsRemainOrdinaryMessages(string text)
    {
        Assert.Equal(ChatControlCommandKind.None, ChatControlCommand.Parse(text).Kind);
    }

    [Fact]
    public void MalformedAnswerPrefixIsReservedForTheInputBroker()
    {
        Assert.Equal(ChatControlCommandKind.ReservedInvalid, ChatControlCommand.Parse("answer my question").Kind);
    }
}
