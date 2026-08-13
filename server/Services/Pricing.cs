using System.Text.Json;

namespace CardVault.Services;

public readonly record struct PriceSet(double? Market, double? Low, double? Mid, double? High);

/// <summary>
/// Digs prices out of a raw card payload. TCGplayer keys prices by printing
/// ("normal", "holofoil", "reverseHolofoil", "1stEditionHolofoil", ...), which is
/// why a collection entry stores which variant you actually own.
/// </summary>
public static class Pricing
{
    public static IReadOnlyList<string> AvailableVariants(JsonElement card)
    {
        if (!TryGetTcgPrices(card, out var prices)) return [];
        return prices.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Printings in the order we'd rather quote them when no specific one is asked
    /// for. Without this we'd take whatever key the API listed first, which on older
    /// cards is often the 1st Edition printing — wildly unrepresentative of what a
    /// typical copy is worth.
    /// </summary>
    private static readonly string[] PreferredVariants =
    [
        "normal", "holofoil", "reverseHolofoil",
        "unlimited", "unlimitedHolofoil",
        "1stEdition", "1stEditionNormal", "1stEditionHolofoil",
    ];

    /// <summary>
    /// Price for a specific printing. Falls back to the most representative printing
    /// the card has when the requested one isn't listed, so a value still shows up
    /// rather than a blank.
    /// </summary>
    public static PriceSet ForVariant(JsonElement card, string? variant)
    {
        if (!TryGetTcgPrices(card, out var prices)) return default;

        JsonElement entry;
        if (!string.IsNullOrEmpty(variant) && prices.TryGetProperty(variant, out var exact))
        {
            entry = exact;
        }
        else
        {
            var available = prices.EnumerateObject().ToList();
            if (available.Count == 0) return default;

            var pick = available
                .OrderBy(p => Array.IndexOf(PreferredVariants, p.Name) is var i && i >= 0 ? i : int.MaxValue)
                .First();

            if (pick.Value.ValueKind != JsonValueKind.Object) return default;
            entry = pick.Value;
        }

        return new PriceSet(Num(entry, "market"), Num(entry, "low"), Num(entry, "mid"), Num(entry, "high"));
    }

    /// <summary>European prices from Cardmarket, shown alongside the USD figures.</summary>
    public static double? CardmarketTrend(JsonElement card)
        => card.TryGetProperty("cardmarket", out var cm)
           && cm.ValueKind == JsonValueKind.Object
           && cm.TryGetProperty("prices", out var p)
            ? Num(p, "trendPrice")
            : null;

    public static string? TcgUpdatedAt(JsonElement card)
        => card.TryGetProperty("tcgplayer", out var t)
           && t.ValueKind == JsonValueKind.Object
           && t.TryGetProperty("updatedAt", out var u)
           && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

    private static bool TryGetTcgPrices(JsonElement card, out JsonElement prices)
    {
        prices = default;
        if (card.ValueKind != JsonValueKind.Object) return false;
        if (!card.TryGetProperty("tcgplayer", out var tcg) || tcg.ValueKind != JsonValueKind.Object) return false;
        if (!tcg.TryGetProperty("prices", out prices) || prices.ValueKind != JsonValueKind.Object) return false;
        return true;
    }

    private static double? Num(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
