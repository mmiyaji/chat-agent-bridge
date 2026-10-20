using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Pricing;

namespace ChatAgentBridge.Core.Tests;

public sealed class PricingCatalogManagerTests
{
    [Fact]
    public async Task InitializeSeedsSeparateFilesAndManualOverridesAutomaticForDatedModels()
    {
        using var folder = new TempFolder();
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Catalog("2026-10-21", 7m), Encoding.UTF8, "application/json"),
            Headers = { ETag = new EntityTagHeaderValue("\"v2\"") }
        }, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Catalog("2026-10-22", 8m), Encoding.UTF8, "application/json"),
            Headers = { ETag = new EntityTagHeaderValue("\"v3\"") }
        });
        using var client = new HttpClient(handler);
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), client, new FakeClock());
        await manager.InitializeAsync();

        Assert.True(File.Exists(manager.AutomaticFilePath));
        Assert.True(File.Exists(manager.ManualFilePath));
        Assert.True(await manager.RefreshAsync(force: true));
        var manual = new ModelPrice("sample-model", new(1, 2), new(3, 4), new(5, 6), new(7, 8));
        await manager.SaveManualAsync(manual);
        var manualFileBeforeRefresh = await File.ReadAllBytesAsync(manager.ManualFilePath);

        Assert.True(await manager.RefreshAsync(force: true));

        Assert.True(manager.Current.TryResolve("sample-model-2026-10-22", out var resolved));
        Assert.Equal(manual, resolved);
        Assert.Equal(new PricingRate(8, 8), Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "sample-model").Input);
        Assert.Equal(manualFileBeforeRefresh, await File.ReadAllBytesAsync(manager.ManualFilePath));

        using (var reload = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.InternalServerError))), new FakeClock()))
        {
            await reload.InitializeAsync();
            Assert.Equal(manual, Assert.Single(reload.Current.ManualPrices));
            Assert.Equal(new PricingRate(8, 8), Assert.Single(reload.Current.AutomaticPrices, x => x.Model == "sample-model").Input);
            Assert.True(reload.Current.TryResolve("sample-model-2026-10-23", out var afterReload));
            Assert.Equal(manual, afterReload);
        }

        await manager.RemoveManualAsync("sample-model");
        Assert.True(manager.Current.TryResolve("sample-model-2026-10-22", out resolved));
        Assert.Equal(new ModelPrice("sample-model", new(8, 8), new(0.7m, 0.7m), new(8, 8), new(80, 80)), resolved);
        Assert.False(manager.Current.TryResolve("unknown-model", out _));
    }

    [Fact]
    public async Task ReloadRestoresManualRowsAndInvalidManualFileIsPreservedAndBlocksEdits()
    {
        using var folder = new TempFolder();
        var price = new ModelPrice("private-model", new(1, 1), new(0, 0), new(1, 1), new(2, 2));
        using (var first = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock()))
        {
            await first.InitializeAsync();
            await first.SaveManualAsync(price);
        }
        using (var reload = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock()))
        {
            await reload.InitializeAsync();
            Assert.Equal(price, Assert.Single(reload.Current.ManualPrices));
        }

        var bad = Encoding.UTF8.GetBytes("not json; preserve me");
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "pricing-manual.json"), bad);
        using var corrupt = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await corrupt.InitializeAsync();
        Assert.False(corrupt.State.ManualFileHealthy);
        Assert.Equal(bad, await File.ReadAllBytesAsync(corrupt.ManualFilePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => corrupt.SaveManualAsync(price));
        await Assert.ThrowsAsync<InvalidOperationException>(() => corrupt.RemoveManualAsync("private-model"));
    }

    [Fact]
    public async Task RefreshUsesETagAndHonorsIntervalAndDisabledPreferenceButForceBypassesBoth()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Catalog("2026-10-21", 7), Encoding.UTF8, "application/json"),
                Headers = { ETag = new EntityTagHeaderValue("\"rev-2\"") }
            },
            request =>
            {
                Assert.Contains(request.Headers.IfNoneMatch, x => x.Tag == "\"rev-2\"");
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog("2026-10-21", 8)) });
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(handler), clock);
        await manager.InitializeAsync();

        Assert.True(await manager.RefreshAsync(force: true));
        Assert.True(await manager.RefreshAsync(force: true));
        Assert.Equal(2, handler.Requests.Count);
        clock.Advance(TimeSpan.FromHours(23));
        Assert.False(await manager.RefreshAsync());
        Assert.Equal(2, handler.Requests.Count);
        await manager.SetAutoUpdateAsync(false);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(await manager.RefreshAsync());
        Assert.True(await manager.RefreshAsync(force: true));
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(manager.State.AutoUpdateEnabled);
    }

    [Fact]
    public async Task FailuresAreThrottledAndMalformedOrOversizedPayloadKeepsCachedPrices()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog("2026-10-21", 7)) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(handler), clock);
        await manager.InitializeAsync();
        var embeddedModelCount = manager.Current.AutomaticPrices.Count;
        Assert.True(await manager.RefreshAsync(force: true));
        Assert.False(await manager.RefreshAsync(force: true));
        Assert.NotNull(manager.State.LastError);
        Assert.Equal(embeddedModelCount + 1, manager.Current.AutomaticPrices.Count);
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(await manager.RefreshAsync());
        Assert.Equal(3, handler.Requests.Count);
        Assert.NotNull(manager.State.LastError);
    }

    [Fact]
    public async Task ContentLengthAndUnknownLengthOversizeResponsesKeepLastValidCatalog()
    {
        using var folder = new TempFolder();
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog("2026-10-21", 7)) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512 * 1024 + 1]) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[512 * 1024 + 1]) });
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(handler), new FakeClock());
        await manager.InitializeAsync();
        Assert.True(await manager.RefreshAsync(force: true));

        var validPrice = Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "sample-model").Input;
        Assert.False(await manager.RefreshAsync(force: true));
        Assert.Equal(validPrice, Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "sample-model").Input);
        Assert.False(await manager.RefreshAsync(force: true));
        Assert.Equal(validPrice, Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "sample-model").Input);
        Assert.NotNull(manager.State.LastError);
    }

    [Fact]
    public async Task LargeValidRemoteCatalogNearLimitCanBeReloadedFromAutoFile()
    {
        using var folder = new TempFolder();
        var payload = LargeCatalog(1810);
        Assert.InRange(payload.Length, 500_000, 512 * 1024);
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using (var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(handler), new FakeClock()))
        {
            await manager.InitializeAsync();
            Assert.True(await manager.RefreshAsync(force: true));
            Assert.True(new FileInfo(manager.AutomaticFilePath).Length < 512 * 1024 * 8);
        }

        using var reload = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.InternalServerError))), new FakeClock());
        await reload.InitializeAsync();
        Assert.Equal(1810, reload.Current.AutomaticPrices.Count(x => x.Model.StartsWith("large-model-", StringComparison.Ordinal)));
        Assert.Equal(new PricingRate(4, 4), Assert.Single(reload.Current.AutomaticPrices, x => x.Model.StartsWith("large-model-0000", StringComparison.Ordinal)).Input);
    }

    [Fact]
    public async Task OlderLocalOrRemoteCatalogCannotDowngradeBundledPricesAnd304WithoutCacheIsRejected()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var oldCatalog = Catalog("2026-10-19", 0.01m);
        var oldEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            autoUpdateEnabled = true,
            lastCheckedUtc = (DateTimeOffset?)null,
            lastUpdatedUtc = (DateTimeOffset?)null,
            etag = "\"old-tag\"",
            catalogJson = oldCatalog
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "pricing-auto.json"), oldEnvelope);

        var handler = new QueueHandler(
            request =>
            {
                Assert.Empty(request.Headers.IfNoneMatch);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog("2026-10-19", 0.02m)) });
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(handler), new FakeClock());
        await manager.InitializeAsync();
        var bundled = Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "gpt-5.4").Input;
        Assert.False(await manager.RefreshAsync(force: true));
        Assert.Equal(bundled, Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "gpt-5.4").Input);
        Assert.DoesNotContain(manager.Current.AutomaticPrices, x => x.Model == "sample-model");
        Assert.NotNull(manager.State.LastError);

        Assert.False(await manager.RefreshAsync(force: true));
        Assert.Equal(bundled, Assert.Single(manager.Current.AutomaticPrices, x => x.Model == "gpt-5.4").Input);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentManualUpdatesRemainDistinctAndEstimatorUsesPassedSnapshot()
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await manager.InitializeAsync();
        var items = Enumerable.Range(0, 12).Select(i => manager.SaveManualAsync(
            new ModelPrice($"custom-{i}", new(i, i), new(i, i), new(i, i), new(i, i))));
        await Task.WhenAll(items);
        Assert.Equal(12, manager.Current.ManualPrices.Count);
        var snapshot = manager.Current;
        var estimate = AgentCostEstimator.Estimate(new AgentUsage
        {
            Model = "custom-1", Scope = "turn", InputIncludesCache = true,
            InputTokens = 10, OutputTokens = 10, CachedInputTokens = 0, CacheWriteInputTokens = 0
        }, snapshot);
        Assert.Equal(new AgentCostEstimate(0.00002m, 0.00002m), estimate);
        Assert.Contains("custom-1", await File.ReadAllTextAsync(manager.ManualFilePath));
    }

    [Fact]
    public async Task RejectsInvalidManualRatesAndEventExceptionsDoNotBreakWrites()
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await manager.InitializeAsync();
        manager.Changed += (_, _) => throw new InvalidOperationException("observer");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.SaveManualAsync(
            new ModelPrice("bad", new(2, 1), new(0, 0), new(0, 0), new(0, 0))));
        await manager.SaveManualAsync(new ModelPrice("good", new(0, 0), new(0, 0), new(0, 0), new(0, 0)));
        Assert.Contains(manager.Current.ManualPrices, x => x.Model == "good");
    }

    [Fact]
    public async Task ForcedRefreshReloadsHandEditedManualFileAndStartupSurvivesUnavailableDataDirectory()
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.InternalServerError))), new FakeClock());
        await manager.InitializeAsync();
        var handEdited = """{"schemaVersion":1,"models":[{"model":"edited","input":{"min":1,"max":1},"cachedInput":{"min":1,"max":1},"cacheWrite":{"min":1,"max":1},"output":{"min":1,"max":1}}]}""";
        var manualBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(handEdited)).ToArray();
        await File.WriteAllBytesAsync(manager.ManualFilePath, manualBytes);

        Assert.False(await manager.RefreshAsync(force: true));
        Assert.True(manager.State.ManualFileHealthy);
        Assert.Contains(manager.Current.ManualPrices, x => x.Model == "edited");

        var blockedPath = Path.Combine(folder.Path, "settings-path-is-a-file");
        await File.WriteAllTextAsync(blockedPath, "occupied");
        using var unavailable = new PricingCatalogManager(new SettingsStore(blockedPath), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await unavailable.InitializeAsync();
        Assert.NotEmpty(unavailable.Current.AutomaticPrices);
        Assert.False(unavailable.State.ManualFileHealthy);
        Assert.NotNull(unavailable.State.LastError);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"models\":[{\"model\":\"bad\",\"input\":{\"min\":2,\"max\":1},\"cachedInput\":{\"min\":0,\"max\":0},\"cacheWrite\":{\"min\":0,\"max\":0},\"output\":{\"min\":0,\"max\":0}}]}")]
    [InlineData("{\"schemaVersion\":1,\"models\":[{\"model\":\" \",\"input\":{\"min\":0,\"max\":0},\"cachedInput\":{\"min\":0,\"max\":0},\"cacheWrite\":{\"min\":0,\"max\":0},\"output\":{\"min\":0,\"max\":0}}]}")]
    [InlineData("{\"schemaVersion\":1,\"models\":[{\"model\":\"bad\",\"input\":{\"min\":\"1\",\"max\":1},\"cachedInput\":{\"min\":0,\"max\":0},\"cacheWrite\":{\"min\":0,\"max\":0},\"output\":{\"min\":0,\"max\":0}}]}")]
    public async Task InvalidHandEditedManualPricesArePreservedAndDoNotBlockStartup(string json)
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await manager.InitializeAsync();
        var original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(manager.ManualFilePath, original);

        await manager.RefreshAsync(force: true);

        Assert.False(manager.State.ManualFileHealthy);
        Assert.Equal(original, await File.ReadAllBytesAsync(manager.ManualFilePath));
        Assert.NotEmpty(manager.Current.AutomaticPrices);
    }

    [Fact]
    public async Task PricingSnapshotManualBaseModelWinsOverAutomaticExactDatedIdAndEffectiveListMatches()
    {
        var automatic = new ModelPrice("foo-20261020", new(9, 9), new(9, 9), new(9, 9), new(9, 9));
        var manual = new ModelPrice("foo", new(1, 1), new(1, 1), new(1, 1), new(1, 1));
        var snapshot = new PricingSnapshot(new DateOnly(2026, 10, 20), [automatic], [manual]);

        Assert.True(snapshot.TryResolve("foo-20261020", out var resolved));
        Assert.Equal(manual.Input, resolved.Input);
        Assert.Equal(manual.Input, Assert.Single(snapshot.EffectivePrices, x => x.Model == automatic.Model).Input);
    }

    [Fact]
    public async Task CancelledRefreshLeavesCachedFilesUnchanged()
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.OK))), new FakeClock());
        await manager.InitializeAsync();
        var automatic = await File.ReadAllBytesAsync(manager.AutomaticFilePath);
        var manual = await File.ReadAllBytesAsync(manager.ManualFilePath);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RefreshAsync(force: true, cancellationToken: cancel.Token));

        Assert.Equal(automatic, await File.ReadAllBytesAsync(manager.AutomaticFilePath));
        Assert.Equal(manual, await File.ReadAllBytesAsync(manager.ManualFilePath));
    }

    [Fact]
    public async Task ManualWriteThatWouldExceedLimitRollsBackAndLeavesFileReadable()
    {
        using var folder = new TempFolder();
        using var manager = new PricingCatalogManager(new SettingsStore(folder.Path), new HttpClient(new QueueHandler(_ => new(HttpStatusCode.NotFound))), new FakeClock());
        await manager.InitializeAsync();
        var prefix = "{\"schemaVersion\":1,\"models\":[";
        var suffix = "]}";
        var rows = new List<string>();
        for (var i = 0; i < 4096; i++)
        {
            var id = "m" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 190);
            rows.Add($"{{\"model\":\"{id}\",\"input\":{{\"min\":0,\"max\":0}},\"cachedInput\":{{\"min\":0,\"max\":0}},\"cacheWrite\":{{\"min\":0,\"max\":0}},\"output\":{{\"min\":0,\"max\":0}}}}");
            var candidate = Encoding.UTF8.GetBytes(prefix + string.Join(',', rows) + suffix);
            if (candidate.Length > 512 * 1024 - 150) { rows.RemoveAt(rows.Count - 1); break; }
        }
        var initial = Encoding.UTF8.GetBytes(prefix + string.Join(',', rows) + suffix);
        Assert.InRange(initial.Length, 512 * 1024 - 500, 512 * 1024);
        await File.WriteAllBytesAsync(manager.ManualFilePath, initial);
        await manager.RefreshAsync(force: true);
        Assert.True(manager.State.ManualFileHealthy);
        var before = await File.ReadAllBytesAsync(manager.ManualFilePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.SaveManualAsync(
            new ModelPrice("additional-model-" + new string('z', 180),
                new(12345678901234567890m, 12345678901234567890m),
                new(12345678901234567890m, 12345678901234567890m),
                new(12345678901234567890m, 12345678901234567890m),
                new(12345678901234567890m, 12345678901234567890m))));

        Assert.Equal(before, await File.ReadAllBytesAsync(manager.ManualFilePath));
        Assert.Equal(rows.Count, manager.Current.ManualPrices.Count);
    }

    private static string Catalog(string date, decimal input) => $$"""
        {"schemaVersion":1,"checkedOn":"{{date}}","currency":"USD","unitTokens":1000000,
         "sources":{"test":"https://example.com/pricing"},
         "models":[{"model":"sample-model","input":[{{input}},{{input}}],"cachedInput":[0.7,0.7],"cacheWrite":[{{input}},{{input}}],"output":[{{input}}0,{{input}}0],"source":"test"}]}
        """;

    private static byte[] LargeCatalog(int count)
    {
        var builder = new StringBuilder("{\"schemaVersion\":1,\"checkedOn\":\"2026-10-21\",\"currency\":\"USD\",\"unitTokens\":1000000,\"sources\":{\"test\":\"https://example.com/pricing\"},\"models\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(',');
            var suffix = i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            var id = "large-model-" + suffix + new string('x', 166);
            builder.Append("{\"model\":\"").Append(id).Append("\",\"input\":[4,4],\"cachedInput\":[1,1],\"cacheWrite\":[4,4],\"output\":[8,8],\"source\":\"test\"}");
        }
        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = Interlocked.Increment(ref _index) - 1;
            return Task.FromResult(responses[Math.Min(index, responses.Length - 1)](request));
        }
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(payload).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
    }

    private sealed class FakeClock(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-pricing-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
