using ChatAgentBridge.Core.Pricing;

namespace ChatAgentBridge.Core;

public sealed record AgentCostEstimate(decimal MinUsd, decimal MaxUsd);

/// <summary>Estimates token-based cost from one immutable pricing snapshot; never performs network I/O.</summary>
public static class AgentCostEstimator
{
    private static readonly Lazy<PricingSnapshot> EmbeddedPricing = new(() =>
    {
        var catalog = PricingCatalogCodec.Parse(PricingCatalogCodec.EmbeddedBytes());
        return new PricingSnapshot(catalog.CheckedOn, catalog.Models, []);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DateOnly PriceCheckedOn => EmbeddedPricing.Value.CheckedOn;
    internal static PricingSnapshot EmbeddedPricingSnapshot => EmbeddedPricing.Value;

    public static AgentCostEstimate? Estimate(AgentUsage? usage) => Estimate(usage, null);

    public static AgentCostEstimate? Estimate(AgentUsage? usage, PricingSnapshot? pricing)
    {
        pricing ??= EmbeddedPricing.Value;
        if (usage is null || !HasSupportedScope(usage) || !pricing.TryResolve(usage.Model, out var price)) return null;
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

                min = Charge(cached, price.CachedInput.Min) + Charge(written, price.CacheWrite.Min) +
                    Charge(remainder, allowedMinRates.Min()) + Charge(output, price.Output.Min);
                max = Charge(cached, price.CachedInput.Max) + Charge(written, price.CacheWrite.Max) +
                    Charge(remainder, allowedMaxRates.Max()) + Charge(output, price.Output.Max);
            }

            return min >= 0m && max >= min ? new AgentCostEstimate(min, max) : null;
        }
        catch (OverflowException) { return null; }
    }

    private static bool HasSupportedScope(AgentUsage usage)
        => usage.Scope is "turn" or "last-request" ||
           usage.Source == "claude.stream-json" && usage.Scope == "thread-total";

    private static decimal Charge(long tokens, decimal usdPerMillion) => Charge((decimal)tokens, usdPerMillion);
    private static decimal Charge(decimal tokens, decimal usdPerMillion) => checked(tokens * usdPerMillion / 1_000_000m);
}
