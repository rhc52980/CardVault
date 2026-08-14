using System.Text.Json;

namespace CardVault.Services.PriceSources;

/// <summary>
/// Cardmarket prices, the European market, also carried in the pokemontcg.io
/// payload and until now thrown away apart from a single trend figure.
///
/// Cardmarket doesn't break prices down by printing the way TCGplayer does — it
/// reports one set of figures per card, plus a separate reverse-holo set. Those
/// map onto two printings; anything else a card has is left unpriced by this
/// source rather than guessed at.
///
/// Figures are in euros, which is exactly why price_history stores a currency.
/// </summary>
public sealed class CardmarketPriceSource : IPriceSource
{
    public string Id => "cardmarket";
    public string DisplayName => "Cardmarket";
    public string Currency => "EUR";

    public bool CanPrice(string cardId, JsonElement card) => TryGetPrices(card, out _);

    public Task<IReadOnlyList<SourcedPrice>> GetPricesAsync(string cardId, JsonElement card, CancellationToken ct)
    {
        var results = new List<SourcedPrice>();

        if (TryGetPrices(card, out var p))
        {
            var market = BaseMarket(p);
            if (market is not null)
            {
                results.Add(new SourcedPrice(
                    Variant: BaseVariant(card),
                    Market: market,
                    Low: Num(p, "lowPrice"),
                    Mid: Num(p, "avg30"),
                    High: Num(p, "avg1")));
            }

            var reverse = Num(p, "reverseHoloTrend") ?? Num(p, "reverseHoloSell");
            if (reverse is > 0)
            {
                results.Add(new SourcedPrice(
                    Variant: "reverseHolofoil",
                    Market: reverse,
                    Low: Num(p, "reverseHoloLow"),
                    Mid: Num(p, "reverseHoloAvg30"),
                    High: Num(p, "reverseHoloAvg1")));
            }
        }

        return Task.FromResult<IReadOnlyList<SourcedPrice>>(results);
    }

    /// <summary>
    /// Cardmarket's headline figure for the card.
    ///
    /// trendPrice is normally the right one — their smoothed current value — but it
    /// is not always sane. Secret Wonders Charizard reports a trendPrice of €0.02
    /// against an averageSellPrice of €34.04, which is not a price anyone could buy
    /// at. A trend that far below the actual selling average is treated as bad data
    /// and the average used instead, rather than charting two pence for a Charizard.
    /// </summary>
    private static double? BaseMarket(JsonElement prices)
    {
        var trend = Num(prices, "trendPrice");
        var average = Num(prices, "averageSellPrice");

        if (trend is not > 0) return average;
        if (average is > 0 && trend < average / 10) return average;
        return trend;
    }

    /// <summary>
    /// Which printing Cardmarket's base price refers to.
    ///
    /// Cardmarket quotes one price for the card plus a separate reverse-holo one,
    /// where TCGplayer breaks every printing out. Calling the base price "normal"
    /// invents a printing for cards that only exist as holos, and puts the two
    /// sources on different lines for the same thing. Using the card's first
    /// non-reverse printing keeps them comparable.
    /// </summary>
    private static string BaseVariant(JsonElement card)
    {
        var printings = Pricing.AvailableVariants(card);
        return printings.FirstOrDefault(v => !v.Contains("reverse", StringComparison.OrdinalIgnoreCase))
               ?? "normal";
    }

    private static bool TryGetPrices(JsonElement card, out JsonElement prices)
    {
        prices = default;
        if (card.ValueKind != JsonValueKind.Object) return false;
        if (!card.TryGetProperty("cardmarket", out var cm) || cm.ValueKind != JsonValueKind.Object) return false;
        if (!cm.TryGetProperty("prices", out prices) || prices.ValueKind != JsonValueKind.Object) return false;
        return true;
    }

    private static double? Num(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
