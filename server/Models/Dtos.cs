namespace PokemonVault.Models;

public sealed record AddEntryRequest(
    string CardId,
    int Quantity = 1,
    string Variant = "normal",
    string Condition = "NM",
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null,
    double? ManualValue = null);

public sealed record UpdateEntryRequest(
    int? Quantity = null,
    string? Variant = null,
    string? Condition = null,
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null,
    double? ManualValue = null,
    /// <summary>Set true to clear a manual value and fall back to market price.</summary>
    bool ClearManualValue = false);

/// <summary>
/// Something the catalogue doesn't have — a booster box, an ETB, a Japanese
/// promo. Stored as a synthetic card so the rest of the app treats it normally.
/// </summary>
public sealed record CustomItemRequest(
    string Name,
    string? Category = null,
    string? ImageUrl = null,
    int Quantity = 1,
    string Condition = "NM",
    string? Grade = null,
    double? Value = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null);

/// <summary>A card you own, flattened with everything the grid needs to render it.</summary>
public sealed record CollectionItem(
    long Id,
    string CardId,
    string Name,
    string? SetId,
    string? SetName,
    string? SetSeries,
    string? Number,
    string? Rarity,
    string? Supertype,
    string[] Types,
    string? Hp,
    string? Artist,
    string? ReleaseDate,
    string? ImageSmall,
    string? ImageLarge,
    int Quantity,
    string Variant,
    string Condition,
    string? Grade,
    double? PurchasePrice,
    string? PurchaseDate,
    string? Notes,
    string AddedAt,
    double? MarketPrice,
    double? LowPrice,
    double? HighPrice,
    double? LineValue,
    string? PricesUpdatedAt,
    /// <summary>Your own valuation, which wins over market price when set.</summary>
    double? ManualValue,
    /// <summary>True for items you entered by hand rather than from the catalogue.</summary>
    bool IsCustom);

public sealed record CollectionStats(
    int DistinctCards,
    int TotalCards,
    double TotalMarketValue,
    double TotalPaid,
    double? BiggestGainAmount,
    string? BiggestGainCardName,
    IReadOnlyList<SetBreakdown> BySet,
    IReadOnlyList<ValuePoint> ValueHistory);

public sealed record SetBreakdown(string? SetId, string? SetName, int Cards, double Value);

/// <summary>
/// A set plus how far through it you are. <see cref="PrintedTotal"/> is the number
/// shown on the cards themselves; <see cref="Total"/> includes secret rares, so the
/// two give "printed set" and "master set" completion respectively.
/// </summary>
public sealed record SetSummary(
    string Id,
    string Name,
    string? Series,
    int PrintedTotal,
    int Total,
    string? ReleaseDate,
    string? Logo,
    string? Symbol,
    int OwnedDistinct,
    int OwnedTotal);

public sealed record SetCard(
    string CardId,
    string Name,
    string Number,
    int NumberSort,
    string? Rarity,
    string? Supertype,
    string? ImageSmall,
    double? MarketPrice,
    IReadOnlyList<string> Variants,
    int OwnedQuantity);

public sealed record ValuePoint(string Date, double Value);

public sealed record ApiKeyRequest(string? ApiKey);
