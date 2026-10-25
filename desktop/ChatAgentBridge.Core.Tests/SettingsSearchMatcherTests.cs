using ChatAgentBridge_Desktop.Search;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsSearchMatcherTests
{
    [Fact]
    public void ProviderOnlyMatchRanksProviderScopeAheadOfGenericField()
    {
        var scope = SettingsSearchMatcher.Rank("Codex settings", "Agent settings", [], "Codex");
        var field = SettingsSearchMatcher.Rank("Model", "Agent settings › Codex", [], "Codex");

        Assert.Equal(1, scope);
        Assert.Equal(3, field);
        Assert.True(scope < field);
    }

    [Fact]
    public void MultiwordQueryRequiresEveryTermAcrossEntryFields()
    {
        Assert.Equal(3, SettingsSearchMatcher.Rank("Codex settings", "Agent settings", ["model モデル"], "Codex model"));
        Assert.Equal(-1, SettingsSearchMatcher.Rank("Codex settings", "Agent settings", ["model モデル"], "Codex absent"));
    }

    [Fact]
    public void SameFieldTitleIsDistinguishedByProviderLocation()
    {
        var codexModel = SettingsSearchMatcher.Rank("Model", "Agent settings › Codex", [], "Codex");
        var otherModel = SettingsSearchMatcher.Rank("Model", "Agent settings › Other provider", [], "Codex");

        Assert.Equal(3, codexModel);
        Assert.Equal(-1, otherModel);
    }

    [Fact]
    public void NormalizesFullWidthJapaneseAndEnglishCase()
    {
        Assert.Equal(1, SettingsSearchMatcher.Rank("Ｃｏｄｅｘ settings", "", ["MODEL モデル"], "codex"));
        Assert.Equal(3, SettingsSearchMatcher.Rank("Model", "Agent settings › Codex", ["model モデル"], "ＣＯＤＥＸ　モデル"));
    }

    [Fact]
    public void MissingTermsAndEmptyQueriesDoNotMatch()
    {
        Assert.Equal(-1, SettingsSearchMatcher.Rank("Codex settings", "", ["model"], "Codex absent"));
        Assert.Equal(-1, SettingsSearchMatcher.Rank("Codex settings", "", ["model"], "   "));
        Assert.Equal(-1, SettingsSearchMatcher.Rank("Codex settings", "", ["model"], "　　"));
    }

    [Fact]
    public void MalformedUtf16DoesNotThrowAndValidEmojiCanMatch()
    {
        Assert.Equal(-1, SettingsSearchMatcher.Rank("\uD800", "", ["\uDC00"], "Codex"));
        Assert.Equal(-1, SettingsSearchMatcher.Rank("Codex", "", [], "\uD800"));
        Assert.Equal(3, SettingsSearchMatcher.Rank("Emoji", "", ["😀"], "😀"));
    }

    [Fact]
    public void RankingCallerCanRetainTheOriginalRouteContext()
    {
        var candidate = new SearchCandidate("Model", "Agent settings › Codex", "Agent settings", "Codex", "model-field");
        var ranked = new[] { candidate }
            .Select(entry => (Entry: entry, Rank: SettingsSearchMatcher.Rank(entry.Title, entry.Location, [], "Codex")))
            .Where(result => result.Rank >= 0)
            .OrderBy(result => result.Rank)
            .ToArray();

        Assert.Single(ranked);
        Assert.Same(candidate, ranked[0].Entry);
        Assert.Equal("Agent settings", ranked[0].Entry.PageContext);
        Assert.Equal("Codex", ranked[0].Entry.TabContext);
        Assert.Equal("model-field", ranked[0].Entry.FieldContext);
    }

    private sealed record SearchCandidate(string Title, string Location, string PageContext, string TabContext, string FieldContext);
}
