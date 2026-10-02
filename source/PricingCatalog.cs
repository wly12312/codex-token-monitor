using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

public sealed record PriceEstimate(string Label, double Credits, double Usd, bool Known, string Note);

public static class PricingCatalog
{
    public const string AsOf = "2026-10-02";
    private static readonly Dictionary<string, ModelRate> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6-astra"] = new("GPT-6 Astra", 250, 25, 1250, 10, 1, 50),
        ["gpt-6.1-sol"] = new("GPT-6.1 Sol", 50, 2.5, 250, 2, .1, 10),
        ["gpt-6-sol"] = new("GPT-6 Sol", 50, 5, 250, 2, .2, 10),
        ["gpt-6-luna"] = new("GPT-6 Luna", 2.5, .25, 12.5, .1, .01, .5),
        ["gpt-5.6-sol"] = new("GPT-5.6 Sol", 100, 10, 500, 4, .4, 20),
        ["gpt-5.6-terra"] = new("GPT-5.6 Terra", 50, 5, 300, 2, .2, 12),
        ["gpt-5.6-luna"] = new("GPT-5.6 Luna", 5, .5, 30, .2, .02, 1.2),
        ["gpt-5.5"] = new("GPT-5.5", 125, 12.5, 750, 5, .5, 30),
        ["gpt-5.4"] = new("GPT-5.4", 62.5, 6.25, 375, 2.5, .25, 15),
        ["gpt-5.4-mini"] = new("GPT-5.4 mini", 18.75, 1.875, 113, .75, .075, 4.5)
    };

    public static string CanonicalModel(string model)
    {
        // Only documented date snapshots are aliases. Never merge mini or future variants.
        var normalized = model.Trim().ToLowerInvariant();
        return Regex.Replace(normalized, @"-\d{4}-\d{2}-\d{2}$", "");
    }

    public static string DisplayName(string model)
    {
        var key = CanonicalModel(model);
        return Rates.TryGetValue(key, out var rate) ? rate.Label : model;
    }

    public static PriceEstimate Calculate(string model, TokenUsage usage, string? tier = null, long? requestInput = null)
    {
        var key = CanonicalModel(model);
        if (!Rates.TryGetValue(key, out var rate))
            return new(model, 0, 0, false, "Unknown model rate; tokens are counted, cost is unavailable.");
        if (string.Equals(tier, "ultrafast", StringComparison.OrdinalIgnoreCase) && key != "gpt-6-astra")
            return new(rate.Label, 0, 0, false, "Ultrafast rate unpublished for this model; tokens are counted, cost is unavailable.");
        var cached = Math.Clamp(usage.Cached, 0, usage.Input);
        var writes = Math.Clamp(usage.CacheWrite, 0, usage.Input - cached);
        var ordinary = Math.Max(0, usage.Input - cached - writes);
        var inputCredit = rate.InputCredits;
        var inputUsd = rate.InputUsd;
        var cachedUsd = rate.CachedUsd;
        var outputUsd = rate.OutputUsd;
        var creditFactor = 1d;
        var apiFactor = 1d;
        var notes = new List<string> { $"Reference rates {AsOf}; not a subscription charge" };
        if (key == "gpt-5.6-sol") notes.Add("Published promotional rate; available at least through 2026-11-21");
        // Match exact supported IDs, so future model names never inherit unverified rates.
        var modernRates = key is "gpt-6-astra" or "gpt-6.1-sol" or "gpt-6-sol" or "gpt-6-luna"
            or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna";
        if (modernRates)
        {
            if (requestInput > 272_000)
            {
                inputUsd *= 2; cachedUsd *= 2; outputUsd *= 1.5;
                notes.Add("API long-context multiplier applied per request; credits keep the published standard reference");
            }
            if (string.Equals(tier, "priority", StringComparison.OrdinalIgnoreCase) || string.Equals(tier, "fast", StringComparison.OrdinalIgnoreCase))
            {
                creditFactor = 2; apiFactor = 2;
                notes.Add("Fast: credit billing ×2; API ×2; included subscription usage uses a separate ×2.5 multiplier");
            }
            if (key == "gpt-6-astra" && string.Equals(tier, "ultrafast", StringComparison.OrdinalIgnoreCase))
            {
                creditFactor = 6; apiFactor = 6;
                notes.Add("Ultrafast: credit billing ×6; API ×6; included subscription usage uses a separate ×8 multiplier");
            }
            if (string.Equals(tier, "flex", StringComparison.OrdinalIgnoreCase) || string.Equals(tier, "batch", StringComparison.OrdinalIgnoreCase))
            {
                apiFactor = .5;
                notes.Add("API Flex/Batch ×0.5; credits remain a standard reference");
            }
        }
        if (string.IsNullOrEmpty(tier)) notes.Add("Speed tier absent in log: Standard assumed");
        // Published credit tables do not specify a separate cache-write category.
        // Cache writes are included in input, not added a second time.
        if (writes > 0) notes.Add(modernRates
            ? "Cache writes: ordinary input credit reference; API write rate ×1.25"
            : "Cache writes: ordinary input reference");
        var credits = ((ordinary + writes) * inputCredit + cached * rate.CachedCredits + usage.Output * rate.OutputCredits) / 1_000_000d * creditFactor;
        var usd = ((ordinary + (modernRates ? 1.25 : 1) * writes) * inputUsd + cached * cachedUsd + usage.Output * outputUsd) / 1_000_000d * apiFactor;
        return new(rate.Label, credits, usd, true, string.Join("; ", notes));
    }
}
