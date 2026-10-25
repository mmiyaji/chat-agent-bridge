using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AntigravityResponseCacheTests
{
    private const string Id = "a352fd0c-204e-4d06-8c83-8de46651b3ed";

    [Fact]
    public void LongStreamIsCappedForFallbackButAllChunksRemainVisibleAsProgress()
    {
        using var parser = CreateParser();
        var streamed = new List<AgentRunProgress>();
        var chunk = new string('x', 50_000);

        for (var i = 0; i < 23; i++)
            streamed.AddRange(parser.AddLine(Step(1, "ACTIVE", chunk)));
        parser.AddLine(Result());

        var snapshot = parser.Snapshot();
        Assert.Equal(CliSessionOutputParser.MaxMessageCharacters, snapshot.FinalMessage!.Length);
        Assert.Equal(new string('x', CliSessionOutputParser.MaxMessageCharacters), snapshot.FinalMessage);
        Assert.True(parser.Truncated);
        Assert.Equal(new string('x', chunk.Length * 23), string.Concat(streamed.Where(item => item.Stream == "stdout").Select(item => item.Text)));
    }

    [Fact]
    public void ManySmallChunksStayLinearAndStreamWithoutLoss()
    {
        using var parser = CreateParser();
        var streamed = new List<AgentRunProgress>();
        const int count = 12_000;
        const string chunk = "abcd";

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++)
            streamed.AddRange(parser.AddLine(Step(7, "ACTIVE", chunk)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        parser.AddLine(Result());

        var expected = string.Concat(Enumerable.Repeat(chunk, count));
        Assert.Equal(expected, parser.Snapshot().FinalMessage);
        Assert.Equal(expected, string.Concat(streamed.Where(item => item.Stream == "stdout").Select(item => item.Text)));
        Assert.Equal(1, parser.TrackedStepCount);
        // Allow ample space for JSON/progress objects while catching repeated full-text
        // concatenations (over 500 MB for this stream with the previous string cache).
        Assert.InRange(allocated, 0, count * 4_096L);
    }

    [Fact]
    public void DoneWholeTextDeduplicatesWhileIncrementalDoneAndOtherStepsAreKept()
    {
        using var parser = CreateParser();
        var streamed = new List<AgentRunProgress>();
        streamed.AddRange(parser.AddLine(Step(1, "ACTIVE", "alpha")));
        streamed.AddRange(parser.AddLine(Step(1, "DONE", "alpha"))); // Repeat of the whole step.
        streamed.AddRange(parser.AddLine(Step(1, "ACTIVE", " beta")));
        streamed.AddRange(parser.AddLine(Step(1, "DONE", "!"))); // Incremental terminal delta.
        streamed.AddRange(parser.AddLine(Step(2, "DONE", "alpha beta!"))); // DONE without ACTIVE.
        streamed.AddRange(parser.AddLine(Step(2, "DONE", "alpha beta!")));
        streamed.AddRange(parser.AddLine(Step(2, "DONE", ""))); // Empty trailing update.
        parser.AddLine(Result());

        Assert.Equal("alpha beta!\nalpha beta!", parser.Snapshot().FinalMessage);
        Assert.Equal("alpha beta!alpha beta!", string.Concat(streamed.Where(item => item.Stream == "stdout").Select(item => item.Text)));
    }

    [Fact]
    public void MissingAndNegativeStepIndexesStreamSafelyWithoutSharingDedupState()
    {
        using var parser = CreateParser();
        var streamed = new List<AgentRunProgress>();
        streamed.AddRange(parser.AddLine(Step(null, "ACTIVE", "A")));
        streamed.AddRange(parser.AddLine(Step(-1, "DONE", "B")));
        parser.AddLine(Result());

        Assert.Equal("AB", parser.Snapshot().FinalMessage);
        Assert.Equal("AB", string.Concat(streamed.Where(item => item.Stream == "stdout").Select(item => item.Text)));
        Assert.Equal(0, parser.TrackedStepCount);
    }

    [Fact]
    public void DuplicateTrackingStopsAtTheConfiguredStepLimitWithoutDroppingProgress()
    {
        using var parser = CreateParser();
        var streamedLength = 0;

        for (var i = 0; i < 5_000; i++)
            streamedLength += parser.AddLine(Step(i, "DONE", "x")).Where(item => item.Stream == "stdout").Sum(item => item.Text.Length);
        parser.AddLine(Result());

        Assert.Equal(4_096, parser.TrackedStepCount);
        Assert.Equal(5_000, streamedLength);
        Assert.Equal(9_999, parser.Snapshot().FinalMessage!.Length);
    }

    [Fact]
    public void FingerprintComparisonMatchesWholeUnicodeTextAcrossChunks()
    {
        using var parser = CreateParser();
        var streamed = new List<AgentRunProgress>();
        streamed.AddRange(parser.AddLine(Step(3, "ACTIVE", "😀日本語")));
        streamed.AddRange(parser.AddLine(Step(3, "ACTIVE", "e\u0301")));
        streamed.AddRange(parser.AddLine(Step(3, "DONE", "😀日本語e\u0301")));
        parser.AddLine(Result());

        Assert.Equal("😀日本語e\u0301", parser.Snapshot().FinalMessage);
        Assert.Equal("😀日本語e\u0301", string.Concat(streamed.Where(item => item.Stream == "stdout").Select(item => item.Text)));
    }

    [Fact]
    public void DisposeReleasesFingerprintStateAndLeavesSnapshotReadable()
    {
        var parser = CreateParser();
        parser.AddLine(Step(4, "ACTIVE", "kept"));
        parser.AddLine(Result());
        var snapshot = parser.Snapshot();

        parser.Dispose();

        Assert.Equal(snapshot.FinalMessage, parser.Snapshot().FinalMessage);
        Assert.Equal(0, parser.TrackedStepCount);
        Assert.Throws<ObjectDisposedException>(() => parser.AddLine(Step(4, "DONE", "kept")));
        parser.Dispose();
    }

    private static CliSessionOutputParser.Accumulator CreateParser()
    {
        var parser = new CliSessionOutputParser.Accumulator(AgentProfile.Antigravity);
        parser.AddLine(JsonSerializer.Serialize(new { @event = "init", conversation_id = Id }));
        return parser;
    }

    private static string Step(int? index, string state, string delta)
    {
        var step = new Dictionary<string, object?>
        {
            ["state"] = state,
            ["step_type"] = "agent_response",
            ["text_delta"] = delta
        };
        if (index is { } value) step["step_index"] = value;
        return JsonSerializer.Serialize(new { @event = "step_update", step_update = step });
    }

    private static string Result()
        => JsonSerializer.Serialize(new { @event = "result", result = new { conversation_id = Id, status = "SUCCESS", response = " " } });
}
