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
    [InlineData("help", ChatControlCommandKind.Help)]
    [InlineData("HELP", ChatControlCommandKind.Help)]
    [InlineData("!cab help", ChatControlCommandKind.Help)]
    [InlineData("command", ChatControlCommandKind.Help)]
    [InlineData("!cab command", ChatControlCommandKind.Help)]
    [InlineData("commands", ChatControlCommandKind.Help)]
    [InlineData("!cab commands", ChatControlCommandKind.Help)]
    [InlineData("!cab model", ChatControlCommandKind.Model)]
    [InlineData("model list", ChatControlCommandKind.Model)]
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
    [InlineData("!cab model gpt-test", "gpt-test")]
    [InlineData("MODEL claude-test", "claude-test")]
    [InlineData("!cab model default", "default")]
    [InlineData("!cab model", "")]
    [InlineData("!cab model unknown extra", "unknown extra")]
    public void ModelCommandsNeverBecomeAgentPrompts(string text, string argument)
    {
        var command = ChatControlCommand.Parse(text);
        Assert.Equal(ChatControlCommandKind.Model, command.Kind);
        Assert.Equal(argument, command.ModelArgument);
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
    [InlineData("help me with this")]
    [InlineData("!cab help me")]
    [InlineData("commands please")]
    public void NonCommandsRemainOrdinaryMessages(string text)
    {
        Assert.Equal(ChatControlCommandKind.None, ChatControlCommand.Parse(text).Kind);
    }

    [Theory]
    [InlineData("!cab remember use tabs", ChatControlCommandKind.Remember, "use tabs")]
    [InlineData("remember", ChatControlCommandKind.Remember, "")]
    [InlineData("!cab forget 2", ChatControlCommandKind.Forget, "2")]
    [InlineData("FORGET all", ChatControlCommandKind.Forget, "all")]
    [InlineData("!cab memory", ChatControlCommandKind.Memory, null)]
    [InlineData("!cab usage", ChatControlCommandKind.Usage, null)]
    public void ParsesMemoryAndUsageCommands(string text, ChatControlCommandKind kind, string? argument)
    {
        var command = ChatControlCommand.Parse(text);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(argument, command.TextArgument);
    }

    [Theory]
    [InlineData("memory leak in parser")]
    [InlineData("usage of this API?")]
    public void ExtraWordsAfterMemoryOrUsageStayOrdinary(string text)
    {
        Assert.Equal(ChatControlCommandKind.None, ChatControlCommand.Parse(text).Kind);
    }

    [Fact]
    public void MalformedAnswerPrefixIsReservedForTheInputBroker()
    {
        Assert.Equal(ChatControlCommandKind.ReservedInvalid, ChatControlCommand.Parse("answer my question").Kind);
    }
}
