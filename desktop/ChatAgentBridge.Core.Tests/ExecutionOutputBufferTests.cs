namespace ChatAgentBridge.Core.Tests;

public sealed class ExecutionOutputBufferTests
{
    [Fact]
    public void KeepsTheLatestTextAndReportsTruncation()
    {
        var buffer = new ExecutionOutputBuffer(8);
        buffer.Append("12345");
        buffer.Append("67890");
        var snapshot = buffer.SnapshotIfChanged(-1)!;
        Assert.Equal("34567890", snapshot.Text);
        Assert.True(snapshot.IsTruncated);
        Assert.Null(buffer.SnapshotIfChanged(snapshot.Version));
        buffer.Append("abcdefghijk");
        Assert.Equal("defghijk", buffer.SnapshotIfChanged(snapshot.Version)!.Text);
    }

    [Fact]
    public void ClearRemovesTheOldRunAndTruncationFlag()
    {
        var buffer = new ExecutionOutputBuffer(3);
        buffer.Append("old output");
        var old = buffer.SnapshotIfChanged(-1)!;
        buffer.Clear();
        var cleared = buffer.SnapshotIfChanged(old.Version)!;
        Assert.Equal("", cleared.Text);
        Assert.False(cleared.IsTruncated);
        buffer.Append("new");
        Assert.Equal("new", buffer.SnapshotIfChanged(cleared.Version)!.Text);
    }

    [Fact]
    public async Task ConcurrentProducersRemainBounded()
    {
        var buffer = new ExecutionOutputBuffer(256);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++) buffer.Append("line\n");
        })));
        var snapshot = buffer.SnapshotIfChanged(-1)!;
        Assert.Equal(256, snapshot.Text.Length);
        Assert.True(snapshot.IsTruncated);
    }

    [Fact]
    public void TailDoesNotStartWithAHalfSurrogatePair()
    {
        var buffer = new ExecutionOutputBuffer(3);
        buffer.Append("\U0001f600abc");
        Assert.Equal("abc", buffer.SnapshotIfChanged(-1)!.Text);
        buffer.Clear();
        buffer.Append("\U0001f600ab");
        Assert.Equal("ab", buffer.SnapshotIfChanged(-1)!.Text);
    }
}
