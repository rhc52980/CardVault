using System.Text.Json;

namespace CardVault.Services.PriceSources;

/// <summary>
/// Prices from live eBay listings, for the things the card catalogue can't price:
/// sealed product and graded slabs entered by hand.
///
/// These are ASKING prices, not sold prices — see <see cref="EbayClient"/> for why
/// no sold-price feed is available. That makes them an upper bound with a long tail
/// of listings that will never sell at the price on them, which is what
/// <see cref="Headline"/> exists to cut through. The id and display name both say
/// "asking" so a figure from here can't quietly be read as a sold comp.
///
/// Deliberately scoped to custom items. Singles already have TCGplayer and
/// Cardmarket for free and with better data, and every eBay call spends a finite
/// daily quota — searching the whole collection would buy noise at real cost.
/// </summary>
public sealed class EbayPriceSource(EbayClient ebay, ILogger<EbayPriceSource> log) : IPriceSource
{
    public string Id => "ebay-asking";
    public string DisplayName => "eBay (asking)";
    public string Currency => "USD";

    /// <summary>How many listings to pull. Deep enough to survive the junk at both ends.</summary>
    private const int SampleSize = 50;

    /// <summary>
    /// Below this many usable listings, no price is reported at all. Two listings
    /// for a rare sealed box tell you what two optimists want, not what it's worth,
    /// and a wrong number here would silently become the item's tracked value.
    /// </summary>
    private const int MinimumSample = 3;

    public bool CanPrice(string cardId, JsonElement card)
        => CustomItemService.IsCustomId(cardId) && ebay.IsConfigured && QueryFor(card) is not null;

    public async Task<IReadOnlyList<SourcedPrice>> GetPricesAsync(
        string cardId, JsonElement card, CancellationToken ct)
    {
        if (QueryFor(card) is not { } query) return [];

        var listings = await ebay.SearchAsync(query, SampleSize, ct);

        // Only US dollars. eBay will happily return a listing priced in another
        // currency, and this source promises USD.
        var prices = listings
            .Where(l => l.Currency == Currency && l.Price > 0 && Matches(query, l.Title))
            .Select(l => l.Price)
            .OrderBy(p => p)
            .ToList();

        if (prices.Count < MinimumSample)
        {
            log.LogDebug("eBay had only {Count} usable listing(s) for {Query}; not pricing", prices.Count, query);
            return [];
        }

        return
        [
            new SourcedPrice(
                // Custom items are all recorded under this one printing, so the
                // snapshot joins onto the collection row.
                Variant: "custom",
                Market: Headline(prices),
                Low: prices[0],
                Mid: Median(prices),
                High: prices[^1]),
        ];
    }

    /// <summary>
    /// The number worth quoting: the median of the cheapest third.
    ///
    /// A plain median of asking prices is dragged upwards by listings that sit
    /// unsold for months, and the single cheapest listing is usually damaged, a
    /// wrong item, or bait. The cheapest third is where things actually move, and
    /// its median ignores an outlier at either end of that band.
    /// </summary>
    internal static double Headline(IReadOnlyList<double> ascendingPrices)
    {
        var take = Math.Max(MinimumSample, ascendingPrices.Count / 3);
        var cheapest = ascendingPrices.Take(take).ToList();
        return Median(cheapest);
    }

    internal static double Median(IReadOnlyList<double> ascendingPrices)
    {
        var count = ascendingPrices.Count;
        var middle = count / 2;
        var value = count % 2 == 1
            ? ascendingPrices[middle]
            : (ascendingPrices[middle - 1] + ascendingPrices[middle]) / 2;
        return Math.Round(value, 2);
    }

    /// <summary>
    /// What to search eBay for. The item's own name is all a custom item has, and
    /// it's what you'd type into eBay yourself — the category ("Sealed", "Custom")
    /// is our own filing, not something sellers put in titles.
    /// </summary>
    internal static string? QueryFor(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object) return null;
        if (!card.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;

        var name = n.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Whether a listing is plausibly the same thing that was searched for.
    ///
    /// eBay pads results with loose matches once the exact ones run out, so a search
    /// for a sealed booster box comes back with single cards, empty boxes and
    /// wrappers — all far cheaper, and all of which would drag the headline price
    /// down hard given it deliberately looks at the cheap end. Requiring every
    /// meaningful word of the name to appear in the title is crude, but it's the
    /// same check a person skimming the results page makes.
    /// </summary>
    internal static bool Matches(string query, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var words = Words(query);
        if (words.Count == 0) return true;

        var titleWords = Words(title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.All(w => titleWords.Contains(w));
    }

    /// <summary>
    /// Meaningful words, lowercased. Single characters and punctuation go, because
    /// "&" or a stray hyphen in our name would exclude every real listing.
    /// </summary>
    private static List<string> Words(string text)
        => text
            .Split((char[])[' ', '\t', '-', '/', ',', '.', '(', ')', '[', ']', ':', ';', '\'', '"', '&', '+'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1)
            .Select(w => w.ToLowerInvariant())
            .ToList();
}
