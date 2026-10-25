using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ModelCatalogFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecommendsRefreshAtSevenDayBoundary()
    {
        var fetchedAt = Now - ModelCatalogFreshness.RefreshAfter;
        Assert.False(ModelCatalogFreshness.IsRefreshRecommended(1, fetchedAt.AddTicks(1), Now));
        Assert.True(ModelCatalogFreshness.IsRefreshRecommended(1, fetchedAt, Now));
        Assert.True(ModelCatalogFreshness.IsRefreshRecommended(1, fetchedAt.AddTicks(-1), Now));
    }

    [Fact]
    public void UnknownAndInvalidTimesRecommendRefreshOnlyForExistingModels()
    {
        foreach (var timestamp in new DateTimeOffset?[] { null, default(DateTimeOffset), Now.AddTicks(1) })
        {
            Assert.Null(ModelCatalogFreshness.ValidFetchedAt(timestamp, Now));
            Assert.True(ModelCatalogFreshness.IsRefreshRecommended(1, timestamp, Now));
            Assert.False(ModelCatalogFreshness.IsRefreshRecommended(0, timestamp, Now));
        }
        Assert.False(ModelCatalogFreshness.IsRefreshRecommended(0, Now.AddDays(-30), Now));
    }

    [Fact]
    public void ComparesInstantsAndNormalizesValidTimestampsToUtc()
    {
        var local = Now.AddDays(-1).ToOffset(TimeSpan.FromHours(9));
        Assert.Equal(TimeSpan.Zero, ModelCatalogFreshness.ValidFetchedAt(local, Now)!.Value.Offset);
        Assert.False(ModelCatalogFreshness.IsRefreshRecommended(1, local, Now));
    }
}
