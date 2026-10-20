using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace ChatAgentBridge.Core;

public sealed record AgentCostEstimate(decimal MinUsd, decimal MaxUsd);

/// <summary>Estimates a USD range from provider-reported token counts and the embedded, dated price catalog.</summary>
public static class AgentCostEstimator
{
    private const string PricingResourceName = "ChatAgentBridge.Core.Pricing.model-prices.json";
    private static readonly Lazy<PricingCatalog> Catalog = new(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DateOnly PriceCheckedOn => Catalog.Value.CheckedOn;

    public static AgentCostEstimate? Estimate(AgentUsage? usage)
    {
        if (usage is null || !HasSupportedScope(usage) || !TryGetPrice(usage.Model, out var price)) return null;
        if (usage.OutputTokens is not >= 0) return null;

        try
        {
            var output = usage.OutputTokens.Value;
            decimal min;
            decimal max;
            if (!usage.InputIncludesCache)
            {
                if (usage.InputTokens is not >= 0 || usage.CachedInputTokens is not >= 0 || usage.CacheWriteInputTokens is not >= 0)
                    return null;
                min = Charge(usage.InputTokens.Value, price.Input.Min) +
                    Charge(usage.CachedInputTokens.Value, price.CachedInput.Min) +
                    Charge(usage.CacheWriteInputTokens.Value, price.CacheWrite.Min) +
                    Charge(output, price.Output.Min);
                max = Charge(usage.InputTokens.Value, price.Input.Max) +
                    Charge(usage.CachedInputTokens.Value, price.CachedInput.Max) +
                    Charge(usage.CacheWriteInputTokens.Value, price.CacheWrite.Max) +
                    Charge(output, price.Output.Max);
            }
            else
            {
                if (usage.InputTokens is not >= 0) return null;
                if (usage.CachedInputTokens is < 0 || usage.CacheWriteInputTokens is < 0) return null;
                var totalInput = (decimal)usage.InputTokens.Value;
                var cached = usage.CachedInputTokens is long read ? (decimal)read : 0m;
                var written = usage.CacheWriteInputTokens is long write ? (decimal)write : 0m;
                if (cached + written > totalInput) return null;

                var remainder = totalInput - cached - written;
                var allowedMinRates = new List<decimal>(3) { price.Input.Min };
                var allowedMaxRates = new List<decimal>(3) { price.Input.Max };
                if (usage.CachedInputTokens is null)
                {
                    allowedMinRates.Add(price.CachedInput.Min);
                    allowedMaxRates.Add(price.CachedInput.Max);
                }
                if (usage.CacheWriteInputTokens is null)
                {
                    allowedMinRates.Add(price.CacheWrite.Min);
                    allowedMaxRates.Add(price.CacheWrite.Max);
                }

                min = Charge(cached, price.CachedInput.Min) +
                    Charge(written, price.CacheWrite.Min) +
                    Charge(remainder, allowedMinRates.Min()) +
                    Charge(output, price.Output.Min);
                max = Charge(cached, price.CachedInput.Max) +
                    Charge(written, price.CacheWrite.Max) +
                    Charge(remainder, allowedMaxRates.Max()) +
                    Charge(output, price.Output.Max);
            }

            return min >= 0m && max >= min ? new AgentCostEstimate(min, max) : null;
        }
        catch (OverflowException) { return null; }
    }

    private static bool HasSupportedScope(AgentUsage usage)
        => usage.Scope is "turn" or "last-request" ||
           usage.Source == "claude.stream-json" && usage.Scope == "thread-total";

    private static bool TryGetPrice(string? model, out ModelPrice price)
    {
        price = default!;
        if (string.IsNullOrEmpty(model)) return false;
        var models = Catalog.Value.Models;
        if (models.TryGetValue(model, out price!)) return true;

        string baseModel;
        if (model.Length > 9 && model[^9] == '-' && DateOnly.TryParseExact(model[^8..], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            baseModel = model[..^9];
        }
        else if (model.Length > 11 && model[^11] == '-' && DateOnly.TryParseExact(model[^10..], "yyyy-MM-dd",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            baseModel = model[..^11];
        }
        else return false;

        return models.TryGetValue(baseModel, out price!);
    }

    private static decimal Charge(long tokens, decimal usdPerMillion)
        => Charge((decimal)tokens, usdPerMillion);

    private static decimal Charge(decimal tokens, decimal usdPerMillion)
        => checked(tokens * usdPerMillion / Catalog.Value.UnitTokens);

    private static PricingCatalog LoadCatalog()
    {
        using var stream = typeof(AgentCostEstimator).Assembly.GetManifestResourceStream(PricingResourceName)
            ?? throw new InvalidDataException($"Embedded pricing catalog '{PricingResourceName}' is missing.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (ReadInt(root, "schemaVersion") != 1 || ReadString(root, "currency") != "USD")
            throw new InvalidDataException("The embedded model pricing catalog has an unsupported schema or currency.");
        if (!DateOnly.TryParseExact(ReadString(root, "checkedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var checkedOn))
            throw new InvalidDataException("The embedded model pricing catalog has an invalid checked-on date.");
        var unitTokens = ReadDecimal(root, "unitTokens");
        if (unitTokens <= 0m) throw new InvalidDataException("The embedded model pricing catalog has an invalid token unit.");

        var sourcesElement = Property(root, "sources");
        if (sourcesElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The embedded pricing catalog has no sources.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourcesElement.EnumerateObject())
        {
            if (source.Value.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(source.Value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("The embedded pricing catalog contains an invalid source URL.");
            sources.Add(source.Name);
        }

        var modelsElement = Property(root, "models");
        if (modelsElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The embedded pricing catalog has no model list.");
        var models = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
        foreach (var item in modelsElement.EnumerateArray())
        {
            var model = ReadString(item, "model");
            var source = ReadString(item, "source");
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(source) || !sources.Contains(source))
                throw new InvalidDataException("The embedded pricing catalog contains a model with an invalid source or ID.");
            var price = new ModelPrice(ReadRate(item, "input"), ReadRate(item, "cachedInput"),
                ReadRate(item, "cacheWrite"), ReadRate(item, "output"));
            if (!models.TryAdd(model, price)) throw new InvalidDataException($"The embedded pricing catalog duplicates model '{model}'.");
        }
        if (models.Count == 0) throw new InvalidDataException("The embedded pricing catalog is empty.");
        return new PricingCatalog(checkedOn, unitTokens, new ReadOnlyDictionary<string, ModelPrice>(models));
    }

    private static RateRange ReadRate(JsonElement model, string name)
    {
        var value = Property(model, name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            throw new InvalidDataException($"The embedded pricing catalog has an invalid '{name}' rate.");
        var values = value.EnumerateArray().ToArray();
        if (!values[0].TryGetDecimal(out var min) || !values[1].TryGetDecimal(out var max) || min < 0m || max < min)
            throw new InvalidDataException($"The embedded pricing catalog has an invalid '{name}' range.");
        return new RateRange(min, max);
    }

    private static int ReadInt(JsonElement value, string name)
        => Property(value, name).TryGetInt32(out var number) ? number : -1;

    private static decimal ReadDecimal(JsonElement value, string name)
        => Property(value, name).TryGetDecimal(out var number) ? number : 0m;

    private static string? ReadString(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;

    private sealed record PricingCatalog(DateOnly CheckedOn, decimal UnitTokens,
        IReadOnlyDictionary<string, ModelPrice> Models);
    private sealed record ModelPrice(RateRange Input, RateRange CachedInput, RateRange CacheWrite, RateRange Output);
    private sealed record RateRange(decimal Min, decimal Max);
}
