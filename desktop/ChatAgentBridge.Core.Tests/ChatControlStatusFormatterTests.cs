using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatControlStatusFormatterTests
{
    [Fact]
    public void FormatLimitsRowsAndReportsOmittedLiveRequests()
    {
        var rows = Enumerable.Range(1, 12).Select(index => $"request {index}").ToArray();

        var result = ChatControlStatusFormatter.Format("12 live requests", rows,
            totalRows: rows.Length, remaining => $"and {remaining} more");
        var lines = result.Split(Environment.NewLine);

        Assert.Equal(12, lines.Length); // Header, ten request rows, and one omission line.
        Assert.Equal("request 10", lines[10]);
        Assert.Equal("and 2 more", lines[11]);
        Assert.DoesNotContain("request 11", result);
    }

    [Fact]
    public void FormatCapsCharactersWithoutSplittingSurrogatePair()
    {
        var rows = new[] { "request 😀 with a long detail" };
        var expectedPrefix = "header" + Environment.NewLine + "request ";

        var result = ChatControlStatusFormatter.Format("header", rows,
            totalRows: rows.Length, _ => "more", maxCharacters: expectedPrefix.Length + 2);

        Assert.Equal(expectedPrefix + "…", result);
        Assert.EndsWith("…", result);
        Assert.False(char.IsHighSurrogate(result[^2]));
    }
}
