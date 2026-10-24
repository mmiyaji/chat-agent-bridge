using ChatAgentBridge_Desktop.Inbox;

namespace ChatAgentBridge.Core.Tests;

public sealed class InboxListPromptTests
{
    [Theory]
    [InlineData("  hello\r\nworld  ", "hello  world")]
    [InlineData("\t\r\n  ", "")]
    [InlineData("\u2003leading and trailing\u2003", "leading and trailing")]
    public void Get_preserves_normalized_trimmed_prompt(string prompt, string expected)
    {
        Assert.Equal(expected, InboxListPrompt.Get(prompt));
    }

    [Fact]
    public void Get_bounds_long_prompt_before_allocating_full_normalized_copy()
    {
        var prompt = "  " + new string('a', 118) + "\r\n" + new string('b', 1_000_000) + "  ";

        var result = InboxListPrompt.Get(prompt);

        Assert.Equal(120, result.Length);
        Assert.Equal(new string('a', 118) + "  ", result);
    }

    [Fact]
    public void Get_handles_many_conversation_titles_without_changing_prefix_semantics()
    {
        var prompts = Enumerable.Range(0, 500)
            .Select(index => $"  conversation-{index:D3}\r\n{new string('x', 256)}  ")
            .ToArray();

        var titles = prompts.Select(InboxListPrompt.Get).ToArray();

        Assert.Equal(500, titles.Length);
        Assert.Equal("conversation-000  " + new string('x', 102), titles[0]);
        Assert.Equal("conversation-499  " + new string('x', 102), titles[^1]);
    }
}
