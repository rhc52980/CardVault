namespace PokemonVault.Models;

public sealed record AddEntryRequest(
    string CardId,
    int Quantity = 1,
    string Variant = "normal",
    string Condition = "NM",
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null);

public sealed record UpdateEntryRequest(
    int? Quantity = null,
    string? Variant = null,
    string? Condition = null,
    string? Grade = null,
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
    string? PricesUpdatedAt);

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

public sealed record ValuePoint(string Date, double Value);
