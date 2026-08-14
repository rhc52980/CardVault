using System.Text.Json;

namespace CardVault.Services.PriceSources;

/// <summary>A price for one printing, from one market, on one day.</summary>
public sealed record SourcedPrice(
    string Variant,
    double? Market,
    double? Low,
    double? Mid,
    double? High);

/// <summary>
/// Somewhere prices can come from.
///
/// Deliberately narrow: a source is handed a card and returns prices per printing.
/// It doesn't identify cards, which stays the catalogue's job — mixing the two is
/// what makes a second provider expensive to add.
/// </summary>
public interface IPriceSource
{
    /// <summary>Stable id, stored in price_history. Never change it once shipped.</summary>
    string Id { get; }

    /// <summary>Shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// ISO currency of everything this source returns. Sources are never summed
    /// across currencies — valuation picks one source and stays in its money.
    /// </summary>
    string Currency { get; }

    /// <summary>
    /// Whether the source can price this card at all. Sources that call out to a
    /// marketplace may want to skip hand-entered items, for instance.
    /// </summary>
    bool CanPrice(string cardId, JsonElement card);

    /// <summary>
    /// Today's prices for each printing this source knows about. Returning nothing
    /// is normal and not an error — plenty of cards aren't listed everywhere.
    /// </summary>
    Task<IReadOnlyList<SourcedPrice>> GetPricesAsync(string cardId, JsonElement card, CancellationToken ct);
}
