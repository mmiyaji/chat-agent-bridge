using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatAgentBridge.Core.Pricing;

public sealed record PricingManagerState(bool AutoUpdateEnabled, DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? LastUpdatedUtc, string? LastError, bool ManualFileHealthy);

/// <summary>Maintains automatic and user-managed pricing in separate local files.</summary>
public sealed class PricingCatalogManager : IDisposable
{
    public static readonly Uri UpdateEndpoint = new("https://mmiyaji.github.io/chat-agent-bridge/pricing/models.json");
    private static readonly Uri LegacyUpdateEndpoint = new("https://raw.githubusercontent.com/mmiyaji/chat-agent-bridge/main/desktop/ChatAgentBridge.Core/Pricing/model-prices.json");
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);
    private const int MaxAutoFileBytes = PricingCatalogCodec.MaxBytes * 8;
    private static readonly ParsedPricingCatalog Embedded = PricingCatalogCodec.Parse(PricingCatalogCodec.EmbeddedBytes());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private PricingSnapshot _current;
    private PricingManagerState _state = new(true, null, null, null, true);
    private ParsedPricingCatalog _automatic;
    private List<ModelPrice> _manual = [];
    private string? _etag;
    private Uri? _etagEndpoint;
    private bool _manualHealthy = true;
    private bool _initialized;
    private bool _disposed;

    public PricingCatalogManager(SettingsStore settingsStore, HttpClient? client = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        AutomaticFilePath = Path.Combine(settingsStore.DirectoryPath, "pricing-auto.json");
        ManualFilePath = Path.Combine(settingsStore.DirectoryPath, "pricing-manual.json");
        _clock = clock ?? TimeProvider.System;
        if (client is null)
        {
            _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            _client.Timeout = Timeout.InfiniteTimeSpan;
            _ownsClient = true;
        }
        else _client = client;
        _automatic = PricingCatalogCodec.Parse(PricingCatalogCodec.EmbeddedBytes());
        _current = MakeSnapshot(_automatic, _manual);
    }

    public string AutomaticFilePath { get; }
    public string ManualFilePath { get; }
    public PricingSnapshot Current => Volatile.Read(ref _current);
    public PricingManagerState State => Volatile.Read(ref _state);
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var errors = new List<string>();
            var autoFileExists = File.Exists(AutomaticFilePath);
            if (autoFileExists)
            {
                try
                {
                    var envelope = await ReadAutoFileAsync(cancellationToken).ConfigureAwait(false);
                    var cached = PricingCatalogCodec.Parse(Encoding.UTF8.GetBytes(envelope.CatalogJson));
                    if (cached.CheckedOn < Embedded.CheckedOn)
                    {
                        _automatic = Embedded;
                        _etag = null;
                        _etagEndpoint = null;
                        errors.Add("Cached automatic prices are older than the bundled catalog; bundled prices are in use.");
                        _state = new PricingManagerState(envelope.AutoUpdateEnabled, null, null,
                            "Cached automatic prices are older than the bundled catalog.", true);
                    }
                    else
                    {
                        _automatic = cached;
                        _state = new PricingManagerState(envelope.AutoUpdateEnabled, envelope.LastCheckedUtc,
                            envelope.LastUpdatedUtc, null, true);
                        _etag = envelope.ETag;
                        _etagEndpoint = ResolveCachedEtagEndpoint(envelope.ETag, envelope.EtagEndpoint);
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    errors.Add("Automatic pricing cache could not be loaded; embedded prices are in use.");
                    _automatic = PricingCatalogCodec.Parse(PricingCatalogCodec.EmbeddedBytes());
                    _state = _state with { LastError = ex.Message };
                }
            }
            if (!File.Exists(AutomaticFilePath))
            {
                try { await WriteAutoFileAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsRecoverable(ex)) { errors.Add("Automatic pricing cache could not be saved; embedded prices are in use."); }
            }

            if (File.Exists(ManualFilePath))
            {
                try { _manual = await ReadManualFileAsync(cancellationToken).ConfigureAwait(false); _manualHealthy = true; }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    _manual = [];
                    _manualHealthy = false;
                    errors.Add("Manual pricing file is invalid; it was preserved and manual edits are disabled until repaired.");
                    _state = _state with { LastError = ex.Message };
                }
            }
            else
            {
                _manual = [];
                _manualHealthy = true;
                try { await WriteManualFileAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    _manualHealthy = false;
                    errors.Add("Manual pricing file could not be created; manual edits are unavailable.");
                }
            }
            _current = MakeSnapshot(_automatic, _manual);
            _state = _state with { ManualFileHealthy = _manualHealthy, LastError = errors.Count == 0 ? _state.LastError : string.Join(" ", errors) };
            _initialized = true;
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    /// <summary>Refreshes when due and enabled; force bypasses both the preference and the 24-hour interval.</summary>
    public async Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var shouldNotify = false;
        var refreshed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (force) await ReloadManualFileAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var lastChecked = _state.LastCheckedUtc;
            if (!force && (!_state.AutoUpdateEnabled || lastChecked is not null && now - lastChecked < UpdateInterval)) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var pagesRequest = CreateUpdateRequest(UpdateEndpoint, out var pagesConditionalTag);
                using var pagesResponse = await _client.SendAsync(pagesRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (pagesResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    using var fallbackRequest = CreateUpdateRequest(LegacyUpdateEndpoint, out var fallbackConditionalTag);
                    using var fallbackResponse = await _client.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    await ApplyPricingResponseAsync(fallbackResponse, LegacyUpdateEndpoint, fallbackConditionalTag).ConfigureAwait(false);
                }
                else
                {
                    await ApplyPricingResponseAsync(pagesResponse, UpdateEndpoint, pagesConditionalTag).ConfigureAwait(false);
                }

                async Task ApplyPricingResponseAsync(HttpResponseMessage response, Uri source, bool sentConditionalTag)
                {
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        if (!sentConditionalTag || _etagEndpoint != source || _state.LastUpdatedUtc is null || _automatic.CheckedOn < Embedded.CheckedOn)
                            throw new InvalidDataException("Server returned 304 without a valid downloaded pricing cache.");
                        _state = _state with { LastCheckedUtc = now, LastError = null };
                        await WriteAutoFileAsync(cancellationToken).ConfigureAwait(false);
                        shouldNotify = true;
                        refreshed = true;
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > PricingCatalogCodec.MaxBytes)
                        throw new InvalidDataException("Remote pricing catalog exceeds the size limit.");
                    var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    var next = PricingCatalogCodec.Parse(bytes);
                    var minimumDate = _automatic.CheckedOn > Embedded.CheckedOn ? _automatic.CheckedOn : Embedded.CheckedOn;
                    if (next.CheckedOn < minimumDate) throw new InvalidDataException("Remote pricing catalog is older than the installed catalog.");
                    var oldCatalog = _automatic;
                    var oldEtag = _etag;
                    var oldEtagEndpoint = _etagEndpoint;
                    var oldState = _state;
                    _automatic = next;
                    _etag = response.Headers.ETag?.ToString();
                    _etagEndpoint = _etag is null ? null : source;
                    _state = _state with { LastCheckedUtc = now, LastUpdatedUtc = now, LastError = null };
                    try { await WriteAutoFileAsync(cancellationToken).ConfigureAwait(false); }
                    catch
                    {
                        _automatic = oldCatalog;
                        _etag = oldEtag;
                        _etagEndpoint = oldEtagEndpoint;
                        _state = oldState;
                        throw;
                    }
                    _current = MakeSnapshot(_automatic, _manual);
                    shouldNotify = true;
                    refreshed = true;
                }
            }
            catch (Exception ex) when (IsRecoverable(ex) && !cancellationToken.IsCancellationRequested)
            {
                _state = _state with { LastCheckedUtc = now, LastError = ex.Message };
                try { await WriteAutoFileAsync(cancellationToken).ConfigureAwait(false); } catch (Exception writeEx) when (IsRecoverable(writeEx)) { }
                shouldNotify = true;
            }
        }
        finally { _gate.Release(); }
        if (shouldNotify) RaiseChanged();
        return refreshed;
    }

    public async Task SetAutoUpdateAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var previous = _state;
            _state = _state with { AutoUpdateEnabled = enabled };
            try { await WriteAutoFileAsync(cancellationToken).ConfigureAwait(false); }
            catch { _state = previous; throw; }
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public async Task SaveManualAsync(ModelPrice price, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(price);
        ValidateManualPrice(price);
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await ReloadManualFileAsync(cancellationToken).ConfigureAwait(false);
            EnsureManualHealthy();
            var next = _manual.Where(x => !StringComparer.Ordinal.Equals(x.Model, price.Model)).Append(price).OrderBy(x => x.Model, StringComparer.Ordinal).ToList();
            var old = _manual;
            _manual = next;
            try { await WriteManualFileAsync(cancellationToken).ConfigureAwait(false); }
            catch { _manual = old; throw; }
            _current = MakeSnapshot(_automatic, _manual);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public async Task RemoveManualAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await ReloadManualFileAsync(cancellationToken).ConfigureAwait(false);
            EnsureManualHealthy();
            var next = _manual.Where(x => !StringComparer.Ordinal.Equals(x.Model, model)).ToList();
            if (next.Count == _manual.Count) return;
            var old = _manual;
            _manual = next;
            try { await WriteManualFileAsync(cancellationToken).ConfigureAwait(false); }
            catch { _manual = old; throw; }
            _current = MakeSnapshot(_automatic, _manual);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }

    private async Task<AutoFile> ReadAutoFileAsync(CancellationToken token)
    {
        var bytes = await ReadBoundedFileAsync(AutomaticFilePath, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 20 });
        var root = doc.RootElement;
        if (GetInt(root, "schemaVersion") != 1 || Get(root, "catalogJson").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Automatic pricing cache schema is invalid.");
        var catalogJson = Get(root, "catalogJson").GetString()!;
        var parsed = PricingCatalogCodec.Parse(Encoding.UTF8.GetBytes(catalogJson));
        var checkedAt = Date(root, "lastCheckedUtc");
        var updatedAt = Date(root, "lastUpdatedUtc");
        return new AutoFile(1, Get(root, "autoUpdateEnabled").ValueKind == JsonValueKind.True,
            checkedAt, updatedAt, GetString(root, "etag") ?? GetString(root, "eTag"), parsed.Json)
        {
            EtagEndpoint = GetString(root, "etagEndpoint")
        };
    }

    private async Task<List<ModelPrice>> ReadManualFileAsync(CancellationToken token)
    {
        var bytes = await ReadBoundedFileAsync(ManualFilePath, token).ConfigureAwait(false);
        ReadOnlyMemory<byte> jsonBytes = bytes.AsMemory();
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) jsonBytes = bytes.AsMemory(3);
        using var doc = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (GetInt(root, "schemaVersion") != 1 || Get(root, "models").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Manual pricing file schema is invalid.");
        var list = new List<ModelPrice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Get(root, "models").EnumerateArray())
        {
            var model = GetString(item, "model") ?? "";
            var price = new ModelPrice(model, Rate(item, "input"), Rate(item, "cachedInput"), Rate(item, "cacheWrite"), Rate(item, "output"));
            try { ValidateManualPrice(price); }
            catch (ArgumentException ex) { throw new InvalidDataException("Manual pricing contains an invalid model or rate.", ex); }
            if (!ids.Add(model)) throw new InvalidDataException($"Manual pricing repeats model '{model}'.");
            list.Add(price);
        }
        return list.OrderBy(x => x.Model, StringComparer.Ordinal).ToList();
    }

    private async Task ReloadManualFileAsync(CancellationToken token)
    {
        if (!File.Exists(ManualFilePath))
        {
            var wasHealthy = _manualHealthy;
            _manual = [];
            _manualHealthy = true;
            _current = MakeSnapshot(_automatic, _manual);
            _state = _state with { ManualFileHealthy = true, LastError = wasHealthy ? _state.LastError : null };
            return;
        }
        try
        {
            var wasHealthy = _manualHealthy;
            var latest = await ReadManualFileAsync(token).ConfigureAwait(false);
            _manual = latest;
            _manualHealthy = true;
            _current = MakeSnapshot(_automatic, _manual);
            _state = _state with { ManualFileHealthy = true, LastError = wasHealthy ? _state.LastError : null };
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Keep the last known-good in-memory rows; never overwrite a corrupt hand-edited file.
            _manualHealthy = false;
            _state = _state with { ManualFileHealthy = false, LastError = ex.Message };
        }
    }

    private async Task WriteAutoFileAsync(CancellationToken token)
    {
        var content = new AutoFile(1, _state.AutoUpdateEnabled, _state.LastCheckedUtc, _state.LastUpdatedUtc, _etag, _automatic.Json)
        {
            EtagEndpoint = _etagEndpoint?.AbsoluteUri
        };
        await AtomicWriteAsync(AutomaticFilePath, JsonSerializer.SerializeToUtf8Bytes(content, JsonOptions), token).ConfigureAwait(false);
    }

    private async Task WriteManualFileAsync(CancellationToken token)
    {
        var content = new ManualFile(1, _manual.OrderBy(x => x.Model, StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content, JsonOptions);
        if (bytes.Length > PricingCatalogCodec.MaxBytes)
            throw new InvalidDataException("Manual pricing file would exceed the size limit.");
        await AtomicWriteAsync(ManualFilePath, bytes, token).ConfigureAwait(false);
    }

    private static PricingSnapshot MakeSnapshot(ParsedPricingCatalog automatic, IEnumerable<ModelPrice> manual)
    {
        var merged = Embedded.Models.ToDictionary(x => x.Model, StringComparer.Ordinal);
        foreach (var item in automatic.Models) merged[item.Model] = item;
        return new PricingSnapshot(automatic.CheckedOn, merged.Values, manual);
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("InitializeAsync must complete before using the pricing manager.");
    }

    private void EnsureManualHealthy()
    {
        if (!_manualHealthy) throw new InvalidOperationException($"The manual pricing file is invalid; repair '{ManualFilePath}' before editing prices. {_state.LastError}");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* Observer failures must not abort price operations. */ }
    }

    private static void ValidateManualPrice(ModelPrice price)
    {
        if (string.IsNullOrWhiteSpace(price.Model) || price.Model.Length > 200 || price.Model.Any(char.IsControl))
            throw new ArgumentException("Model ID is invalid.", nameof(price));
        ValidateRate(price.Input, nameof(price.Input));
        ValidateRate(price.CachedInput, nameof(price.CachedInput));
        ValidateRate(price.CacheWrite, nameof(price.CacheWrite));
        ValidateRate(price.Output, nameof(price.Output));
    }

    private static void ValidateRate(PricingRate rate, string name)
    {
        if (rate is null || rate.Min < 0 || rate.Max < rate.Min) throw new ArgumentOutOfRangeException(name, "Rate range must be nonnegative and ordered.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > PricingCatalogCodec.MaxBytes) throw new InvalidDataException("Remote pricing catalog exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        var maxBytes = StringComparer.Ordinal.Equals(Path.GetFileName(path), "pricing-auto.json") ? MaxAutoFileBytes : PricingCatalogCodec.MaxBytes;
        if (!info.Exists || info.Length > maxBytes) throw new InvalidDataException("Pricing file is missing or exceeds the size limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Pricing file is missing or exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsRecoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or HttpRequestException or TaskCanceledException or FormatException or InvalidOperationException;
    private static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static int GetInt(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var v) ? v : -1;
    private static string? GetString(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    private static DateTimeOffset? Date(JsonElement e, string key) => DateTimeOffset.TryParse(GetString(e, key), out var d) ? d.ToUniversalTime() : null;
    private static PricingRate Rate(JsonElement e, string key)
    {
        var v = Get(e, key);
        if (v.ValueKind != JsonValueKind.Object || Get(v, "min").ValueKind != JsonValueKind.Number || Get(v, "max").ValueKind != JsonValueKind.Number ||
            !Get(v, "min").TryGetDecimal(out var min) || !Get(v, "max").TryGetDecimal(out var max))
            throw new InvalidDataException("Manual price range is invalid.");
        return new PricingRate(min, max);
    }

    private HttpRequestMessage CreateUpdateRequest(Uri endpoint, out bool sentConditionalTag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        sentConditionalTag = false;
        if (_etagEndpoint == endpoint && !string.IsNullOrWhiteSpace(_etag) && EntityTagHeaderValue.TryParse(_etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
            sentConditionalTag = true;
        }
        return request;
    }

    private static Uri? ResolveCachedEtagEndpoint(string? etag, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(etag)) return null;
        if (string.IsNullOrWhiteSpace(endpoint)) return LegacyUpdateEndpoint; // Pre-Pages caches only used the raw GitHub URL.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)) return null;
        return parsed == UpdateEndpoint || parsed == LegacyUpdateEndpoint ? parsed : null;
    }

    private sealed record AutoFile(int SchemaVersion, bool AutoUpdateEnabled, DateTimeOffset? LastCheckedUtc, DateTimeOffset? LastUpdatedUtc, string? ETag, string CatalogJson)
    {
        public string? EtagEndpoint { get; init; }
    }
    private sealed record ManualFile(int SchemaVersion, IReadOnlyList<ModelPrice> Models);
}
