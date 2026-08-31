namespace CardVault.Models;

public enum ImportStatus
{
    /// <summary>Resolved to exactly one card.</summary>
    Matched,

    /// <summary>Several cards fit — the user picks which one.</summary>
    Ambiguous,

    /// <summary>
    /// The row's card id resolved, but to a card the rest of the row disagrees with.
    ///
    /// A card carries no pokemontcg.io id anywhere on it, so anything filling in a
    /// Card ID column is inferring it from the set symbol. An inferred id that is
    /// wrong is still perfectly valid — it resolves to exactly one real card and would
    /// otherwise arrive looking every bit as matched as a correct one. This status is
    /// the difference between importing the wrong card and being asked about it.
    /// </summary>
    Mismatch,

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
    IReadOnlyList<string> Variants,
    /// <summary>The set's printed total, used to match a number written "45/094".</summary>
    int? PrintedTotal = null);

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

    /// <summary>
    /// The denominator, when the number was written as it appears on the card —
    /// "45/094". It nearly identifies the set on its own, which is the difference
    /// between a column of numbers resolving outright and every row of it coming
    /// back ambiguous because card 45 exists in almost every set ever printed.
    /// </summary>
    public int? PrintedTotal { get; set; }

    /// <summary>
    /// What the row itself said the card was, kept because resolution overwrites
    /// <see cref="Name"/> and <see cref="Number"/> with the matched card's own values.
    ///
    /// Without these a mismatched row can only describe the disagreement in prose, and
    /// the leftovers export would hand back the card we suspect is wrong rather than
    /// what was actually transcribed off the card.
    /// </summary>
    public string? ClaimedName { get; set; }

    public string? ClaimedNumber { get; set; }

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

    /// <summary>
    /// Printing language as a code, normalised at parse time so the review list shows
    /// what will actually be stored rather than whatever the CSV happened to say.
    /// </summary>
    public string Language { get; set; } = Services.Languages.Default;

    public List<CardCandidate> Candidates { get; set; } = [];
}

public sealed class ImportJob
{
    public required string Id { get; init; }

    /// <summary>
    /// The collection this import was started against.
    ///
    /// Jobs live in memory shared by every vault, and a commit writes cards. Without
    /// this, starting an import, switching collections and pressing commit would put
    /// somebody else's cards in your vault — the one cross-vault mistake that would
    /// actually cost you something.
    /// </summary>
    public string Vault { get; init; } = Data.CurrentVault.Default;

    public string State { get; set; } = "resolving";
    public int Total { get; set; }
    public int Processed { get; set; }
    public string? Error { get; set; }
    public List<string> UnmappedColumns { get; set; } = [];

    /// <summary>
    /// Ignored columns whose names read like something the importer wants. Called out
    /// loudly rather than listed in grey beside the ones you meant to ignore.
    /// </summary>
    public List<string> SuspiciousColumns { get; set; } = [];
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
    string? Location = null,
    string? Language = null,
    /// <summary>
    /// The row this came from, echoed back in <see cref="CommitOutcome"/> so the
    /// review list can mark up individual rows rather than only a total. Defaults to
    /// -1 for callers that don't track rows, such as the tests.
    /// </summary>
    int Index = -1);

public sealed record CommitRequest(IReadOnlyList<CommitRow> Rows);

/// <summary>
/// What became of one row at commit time.
///
/// Reported per row rather than as a total because a commit that adds 97 of 100 owes
/// you the three, and a bare count can't say which. <see cref="Reason"/> is set only
/// when <see cref="Added"/> is false.
/// </summary>
public sealed record CommitOutcome(int Index, string CardId, bool Added, string? Reason);

public sealed record CommitResult(int Added, IReadOnlyList<CommitOutcome> Rows);
