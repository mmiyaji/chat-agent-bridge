namespace ChatAgentBridge.Core;

/// <summary>Age guidance for diagnostic model candidates; it never controls execution or starts discovery.</summary>
public static class ModelCatalogFreshness
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    public static DateTimeOffset? ValidFetchedAt(DateTimeOffset? fetchedAtUtc, DateTimeOffset nowUtc) =>
        fetchedAtUtc is { } fetchedAt && fetchedAt != default && fetchedAt <= nowUtc
            ? fetchedAt.ToUniversalTime() : null;

    public static bool IsRefreshRecommended(int modelCount, DateTimeOffset? fetchedAtUtc, DateTimeOffset nowUtc) =>
        modelCount > 0 && (ValidFetchedAt(fetchedAtUtc, nowUtc) is not { } fetchedAt || nowUtc - fetchedAt >= RefreshAfter);
}
