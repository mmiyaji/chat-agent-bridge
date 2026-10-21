using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace ChatAgentBridge.Core.Pricing;

public sealed record PricingRate(decimal Min, decimal Max);

public sealed record ModelPrice(string Model, PricingRate Input, PricingRate CachedInput,
    PricingRate CacheWrite, PricingRate Output);

/// <summary>An immutable price view captured for a single calculation or history aggregation.</summary>
public sealed class PricingSnapshot
{
    private readonly IReadOnlyDictionary<string, ModelPrice> _manual;
    private readonly IReadOnlyDictionary<string, ModelPrice> _automatic;

    public PricingSnapshot(DateOnly checkedOn, IEnumerable<ModelPrice> automaticPrices, IEnumerable<ModelPrice> manualPrices)
    {
        CheckedOn = checkedOn;
        AutomaticPrices = Array.AsReadOnly(automaticPrices.ToArray());
        ManualPrices = Array.AsReadOnly(manualPrices.ToArray());
        _automatic = new ReadOnlyDictionary<string, ModelPrice>(AutomaticPrices.ToDictionary(x => x.Model, StringComparer.Ordinal));
        _manual = new ReadOnlyDictionary<string, ModelPrice>(ManualPrices.ToDictionary(x => x.Model, StringComparer.Ordinal));
        var effective = new List<ModelPrice>();
        foreach (var model in _automatic.Keys.Concat(_manual.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (TryFind(_manual, model, out var resolved) || TryFind(_automatic, model, out resolved))
                effective.Add(resolved with { Model = model });
        }
        EffectivePrices = Array.AsReadOnly(effective.ToArray());
    }

    public DateOnly CheckedOn { get; }
    public IReadOnlyList<ModelPrice> AutomaticPrices { get; }
    public IReadOnlyList<ModelPrice> ManualPrices { get; }
    public IReadOnlyList<ModelPrice> EffectivePrices { get; }

    public bool TryResolve(string? model, out ModelPrice price)
    {
        price = null!;
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (TryFind(_manual, model, out price)) return true;
        return TryFind(_automatic, model, out price);
    }

    private static bool TryFind(IReadOnlyDictionary<string, ModelPrice> prices, string model, out ModelPrice price)
    {
        if (prices.TryGetValue(model, out price!)) return true;
        var baseModel = RemoveValidDateSuffix(model);
        return baseModel is not null && prices.TryGetValue(baseModel, out price!);
    }

    internal static string? RemoveValidDateSuffix(string model)
    {
        if (model.Length > 9 && model[^9] == '-' && DateOnly.TryParseExact(model[^8..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return model[..^9];
        if (model.Length > 11 && model[^11] == '-' && DateOnly.TryParseExact(model[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return model[..^11];
        return null;
    }
}

internal sealed record ParsedPricingCatalog(DateOnly CheckedOn, string Json, IReadOnlyList<ModelPrice> Models);

internal static class PricingCatalogCodec
{
    public const int MaxBytes = 512 * 1024;

    public static ParsedPricingCatalog Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Pricing catalog size is invalid.");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (Int(root, "schemaVersion") != 1 || String(root, "currency") != "USD" || Decimal(root, "unitTokens") != 1_000_000m)
            throw new InvalidDataException("Pricing catalog must use schema 1, USD, and one million token units.");
        if (!DateOnly.TryParseExact(String(root, "checkedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkedOn))
            throw new InvalidDataException("Pricing catalog has an invalid checked-on date.");
        var sourcesElement = Get(root, "sources");
        if (sourcesElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Pricing catalog has no source map.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourcesElement.EnumerateObject())
        {
            if (source.Value.ValueKind != JsonValueKind.String || !Uri.TryCreate(source.Value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Pricing catalog contains a non-HTTPS source.");
            sources.Add(source.Name);
        }
        var modelArray = Get(root, "models");
        if (modelArray.ValueKind != JsonValueKind.Array || modelArray.GetArrayLength() is 0 or > 4096)
            throw new InvalidDataException("Pricing catalog model list is invalid.");
        var models = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
        foreach (var item in modelArray.EnumerateArray())
        {
            var id = String(item, "model");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) || !sources.Contains(String(item, "source") ?? ""))
                throw new InvalidDataException("Pricing catalog contains an invalid model ID or source.");
            var price = new ModelPrice(id, Rate(item, "input"), Rate(item, "cachedInput"), Rate(item, "cacheWrite"), Rate(item, "output"));
            if (!models.TryAdd(id, price)) throw new InvalidDataException($"Pricing catalog duplicates model '{id}'.");
        }
        return new ParsedPricingCatalog(checkedOn, System.Text.Encoding.UTF8.GetString(bytes.Span), Array.AsReadOnly(models.Values.ToArray()));
    }

    public static byte[] EmbeddedBytes()
    {
        using var stream = typeof(PricingCatalogCodec).Assembly.GetManifestResourceStream("ChatAgentBridge.Core.Pricing.model-prices.json")
            ?? throw new InvalidDataException("Embedded pricing catalog is missing.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static PricingRate Rate(JsonElement element, string name)
    {
        var array = Get(element, name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 2) throw new InvalidDataException($"Pricing rate '{name}' is invalid.");
        var values = array.EnumerateArray().ToArray();
        if (!values[0].TryGetDecimal(out var min) || !values[1].TryGetDecimal(out var max) || min < 0m || max < min)
            throw new InvalidDataException($"Pricing rate '{name}' is invalid.");
        return new PricingRate(min, max);
    }

    private static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static int Int(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var v) ? v : -1;
    private static decimal Decimal(JsonElement e, string key) => Get(e, key).TryGetDecimal(out var v) ? v : -1m;
    private static string? String(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
