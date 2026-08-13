namespace PokemonVault.Models;

public enum ImportStatus
{
    /// <summary>Resolved to exactly one card.</summary>
    Matched,

    /// <summary>Several cards fit — the user picks which one.</summary>
    Ambiguous,

    /// <summary>Nothing in the catalogue matched.</summary>
    NotFound,

    /// <summary>
    /// We never got a usable answer — the API errored on every attempt. Distinct from
    /// NotFound on purpose: the card may well exist, so this row is worth retrying
    /// rather than being written off.
    /// </summary>
    LookupFailed,

    /// <summary>The row didn't carry enough information to look anything up.</summary>
    Invalid,
}

public sealed record CardCandidate(
    string CardId,
    string Name,
    string? SetName,
    string? Number,
    string? Rarity,
    string? ImageSmall,
    double? MarketPrice,
    IReadOnlyList<string> Variants);

public sealed class ImportRow
{
    public int Index { get; init; }
    public required string Source { get; init; }
    public ImportStatus Status { get; set; }
    public string? Message { get; set; }

    public string? CardId { get; set; }
    public string? Name { get; set; }
    public string? SetName { get; set; }
    public string? SetId { get; set; }
    public string? SetCode { get; set; }
    public string? Number { get; set; }
    public string? Rarity { get; set; }
    public string? ImageSmall { get; set; }
    public double? MarketPrice { get; set; }
    public IReadOnlyList<string> Variants { get; set; } = [];

    public int Quantity { get; set; } = 1;
    public string Variant { get; set; } = "normal";
    public string Condition { get; set; } = "NM";
    public string? Grade { get; set; }
    public double? PurchasePrice { get; set; }
    public string? PurchaseDate { get; set; }
    public string? Notes { get; set; }
    public string? Location { get; set; }

    public List<CardCandidate> Candidates { get; set; } = [];
}

public sealed class ImportJob
{
    public required string Id { get; init; }
    public string State { get; set; } = "resolving";
    public int Total { get; set; }
    public int Processed { get; set; }
    public string? Error { get; set; }
    public List<string> UnmappedColumns { get; set; } = [];
    public List<ImportRow> Rows { get; set; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ImportRequest(string Csv);

public sealed record CommitRow(
    string CardId,
    int Quantity = 1,
    string Variant = "normal",
    string Condition = "NM",
    string? Grade = null,
    double? PurchasePrice = null,
    string? PurchaseDate = null,
    string? Notes = null,
    string? Location = null);

public sealed record CommitRequest(IReadOnlyList<CommitRow> Rows);
