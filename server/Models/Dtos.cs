namespace CardVault.Models;

public sealed record AddEntryRequest(
    string CardId,
    int Quantity = 1,
    string Variant = "normal",
    string Condition = "NM",
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null,
    double? ManualValue = null,
    string? Location = null,
    /// <summary>
    /// Printing language, as an ISO code. Anything unrecognised — including the
    /// nothing an older client sends — is stored as English.
    /// </summary>
    string? Language = null);

public sealed record UpdateEntryRequest(
    int? Quantity = null,
    string? Variant = null,
    string? Condition = null,
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null,
    double? ManualValue = null,
    string? Location = null,
    string? Language = null,
    /// <summary>Set true to clear a manual value and fall back to market price.</summary>
    bool ClearManualValue = false,
    /// <summary>
    /// Set true to forget what was paid. Needs its own flag for the same reason as
    /// the manual value: a null price means "leave alone", so without this there is
    /// no way to undo a figure entered by mistake.
    /// </summary>
    bool ClearPurchasePrice = false);

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
    string? Notes = null,
    string? Language = null);

/// <summary>One edit applied to every entry named, for acting on a whole selection.</summary>
public sealed record BulkUpdateRequest(
    IReadOnlyList<long> Ids,
    UpdateEntryRequest Update);

public sealed record BulkRemoveRequest(IReadOnlyList<long> Ids);

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
    /// <summary>The grader read out of <see cref="Grade"/>, when it names one.</summary>
    string? GradeCompany,
    /// <summary>The number read out of <see cref="Grade"/>, on the 1-10 scale.</summary>
    double? GradeValue,
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
    bool IsCustom,
    /// <summary>Where the card physically lives, so you can actually find it.</summary>
    string? Location,
    /// <summary>Printing language as an ISO code — "en", "ja", "zh-tw".</summary>
    string Language,
    /// <summary>
    /// True when you've attached your own photograph of this copy. The image itself
    /// comes from its own endpoint rather than riding along in the grid payload.
    /// </summary>
    bool HasPhoto,
    /// <summary>
    /// What a later read of the scans says this card should have been, when that
    /// disagrees with what was imported. A note only — the entry still holds the card
    /// it was imported as, and changing it is your call.
    /// </summary>
    string? Flagged,
    /// <summary>
    /// False when the prices we hold don't describe this copy — a slab, or a printing
    /// in a language none of our sources cover. Such a copy is worth whatever
    /// <see cref="ManualValue"/> says and nothing otherwise, and the UI says so
    /// rather than showing a figure from the wrong market.
    /// </summary>
    bool Priced,
    /// <summary>
    /// The market figure we hold but won't count, when <see cref="Priced"/> is false.
    /// Kept because it's the number you'd judge against when valuing a slab yourself,
    /// and losing it would mean looking the same card up somewhere else.
    /// </summary>
    double? ReferencePrice,
    /// <summary>
    /// Why the figure doesn't count: "graded", "language", or null when it does.
    /// A code rather than a sentence, so the wording lives with the rest of the copy.
    /// </summary>
    string? UnpricedReason,
    /// <summary>
    /// The CSV import this card arrived in, or null if it was added by hand. Cards
    /// from an import you haven't acknowledged yet are marked in the vault.
    /// </summary>
    string? ImportBatch = null);

/// <summary>
/// A CSV import that put cards in the collection, and what has become of them.
///
/// The counts are read from the collection itself rather than stored, so selling or
/// deleting individual cards keeps them honest with nothing needing to be kept in step.
/// </summary>
public sealed record ImportBatchSummary(
    string Id,
    string CreatedAt,
    /// <summary>Null until you've looked over what came in.</summary>
    string? AcknowledgedAt,
    /// <summary>Entries from this import still in the collection.</summary>
    int Entries,
    /// <summary>Cards, counting quantities rather than rows.</summary>
    int Cards,
    /// <summary>How many of those entries you've edited or partly sold since.</summary>
    int Modified);

public sealed record CollectionStats(
    int DistinctCards,
    int TotalCards,
    double TotalMarketValue,
    double TotalPaid,
    double? BiggestGainAmount,
    string? BiggestGainCardName,
    IReadOnlyList<SetBreakdown> BySet,
    IReadOnlyList<ValuePoint> ValueHistory,
    /// <summary>Money actually made on cards you've sold, after fees.</summary>
    double RealisedGain,
    double SaleProceeds,
    int CardsSold);

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

/// <summary>What to call this collection. Empty or null restores the default.</summary>
public sealed record VaultNameRequest(string? Name);

/// <summary>eBay application credentials, entered together since neither works alone.</summary>
public sealed record EbayCredentialsRequest(string? ClientId, string? ClientSecret);

/// <summary>Scrydex credentials — both headers ride on every request.</summary>
public sealed record ScrydexCredentialsRequest(string? ApiKey, string? TeamId);

/// <summary>
/// A card from the offline catalogue. No price field, and that is not an oversight:
/// the source data carries no prices at all, and this exists to find a card rather
/// than to value one.
/// </summary>
public sealed record CatalogueCard(
    string Id,
    string Name,
    string? SetId,
    string? SetName,
    string? SetSeries,
    string? Number,
    int? PrintedTotal,
    string? Rarity,
    string? Supertype,
    string[] Types,
    string? Artist,
    string? ReleaseDate,
    /// <summary>Whether the artwork was downloaded, so the UI knows to expect one.</summary>
    bool HasImage);

/// <summary>
/// How a running download is getting on. <see cref="State"/> is "sets", "images",
/// "done", "failed" or "cancelled".
/// </summary>
public sealed record CatalogueProgress(
    string State,
    int Done,
    int Total,
    string? Detail,
    string? Error);

public sealed record CatalogueStatus(
    bool Enabled,
    int Cards,
    int Images,
    long ImageBytes,
    string? DownloadedAt,
    CatalogueProgress? Progress);

/// <summary>Whether to pull artwork too, which is the slow and large part.</summary>
public sealed record CatalogueDownloadRequest(bool IncludeImages = true);

/// <summary>
/// A price refresh in flight. Refreshing a large collection is one API call per
/// card with pacing between them, so it runs in the background and is polled rather
/// than awaited — a request that sat open for several minutes would simply time out.
/// </summary>
public sealed record PriceRefreshProgress(
    bool Running,
    int Done,
    int Total,
    string? Detail,
    string? Error,
    /// <summary>When the last run ended, so the UI can say "done" rather than just stopping.</summary>
    string? FinishedAt);

public sealed record ToggleRequest(bool Enabled);

/// <summary>
/// Whether card photos are switched on, and what they're costing you in disk.
/// The size is reported because these are full scans, not thumbnails, and a folder
/// quietly growing to several gigabytes is the sort of thing worth being told about.
/// </summary>
public sealed record PhotoStatus(bool Enabled, int Count, long Bytes);

/// <summary>How one import batch compares against the scan file it came from.</summary>
public sealed record BatchReconcile(
    string BatchId,
    /// <summary>The scan CSV this batch was matched to, or null if none fitted.</summary>
    string? ScanFile,
    int Entries,
    /// <summary>Entries the scans still agree with.</summary>
    int Agreed,
    /// <summary>Entries the scans now read differently. Flagged, never changed.</summary>
    int Disagreed,
    /// <summary>Rows in the file with no entry at all — scanned but never imported.</summary>
    int NeverImported);

public sealed record ReconcileReport(
    /// <summary>False for a dry run, which reports without writing a single flag.</summary>
    bool Applied,
    IReadOnlyList<BatchReconcile> Batches,
    int Agreed,
    int Disagreed,
    int NeverImported,
    /// <summary>Files that matched no batch — most likely never imported at all.</summary>
    IReadOnlyList<string> UnmatchedFiles);

public sealed record PasswordRequest(string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AddWantRequest(
    string CardId,
    string Variant = "normal",
    double? TargetPrice = null,
    int Quantity = 1,
    string? Notes = null);

public sealed record UpdateWantRequest(
    double? TargetPrice = null,
    int? Quantity = null,
    string? Notes = null,
    string? Variant = null,
    /// <summary>Set true to drop the target so the card is simply "wanted".</summary>
    bool ClearTarget = false);

public sealed record WantItem(
    long Id,
    string CardId,
    string Name,
    string? SetName,
    string? Number,
    string? Rarity,
    string? ImageSmall,
    string Variant,
    IReadOnlyList<string> Variants,
    int Quantity,
    double? TargetPrice,
    string? Notes,
    string AddedAt,
    double? MarketPrice,
    /// <summary>Market minus target — negative means it's going for less than you'd pay.</summary>
    double? DifferenceToTarget,
    /// <summary>True when the market has come down to your price.</summary>
    bool AtOrBelowTarget,
    /// <summary>
    /// When it first came down and stayed there, or null if it hasn't. A drop this
    /// morning and one that has sat for a month are different situations, and this is
    /// what tells them apart. Reset whenever the price goes back above.
    /// </summary>
    string? MetSince = null);

public sealed record PricePoint(string Date, double Market, double? Low, double? High);

/// <summary>
/// One line on the price chart: a printing, from one market. Currency is carried
/// rather than assumed — these are different markets and their figures are not
/// interchangeable.
/// </summary>
public sealed record PriceSeries(
    string Variant,
    string Source,
    string SourceName,
    string Currency,
    IReadOnlyList<PricePoint> Points);

public sealed record PriceSourceInfo(string Id, string Name, string Currency);

public sealed record PreferredSourceRequest(string? Source);

public sealed record CardHistory(
    string CardId,
    IReadOnlyList<PriceSeries> Series,
    /// <summary>Printings you actually own, so the chart can default to one of those.</summary>
    IReadOnlyList<string> OwnedVariants);

/// <summary>Selling some or all of a collection entry.</summary>
public sealed record SellRequest(
    int Quantity,
    double SalePrice,
    string? SaleDate = null,
    double? Fees = null,
    string? Notes = null);

public sealed record SaleRecord(
    long Id,
    string CardId,
    string CardName,
    string? SetName,
    string? Number,
    string? ImageSmall,
    int Quantity,
    string? Variant,
    string? Condition,
    string? Grade,
    /// <summary>The grader read out of <see cref="Grade"/>, when it names one.</summary>
    string? GradeCompany,
    /// <summary>The number read out of <see cref="Grade"/>, on the 1-10 scale.</summary>
    double? GradeValue,
    /// <summary>Printing language, or null for sales predating the column.</summary>
    string? Language,
    double? PurchasePrice,
    double SalePrice,
    double? Fees,
    string SaleDate,
    string? Notes,
    string RecordedAt,
    /// <summary>Proceeds after fees, minus what you paid. Null if cost is unknown.</summary>
    double? RealisedGain,
    double Proceeds);
