using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// CSV imports as things you can review and undo, rather than a hundred cards that
/// simply appeared.
///
/// Importing a scanned collection means committing cards in batches, and the mistake
/// that matters is not noticing a bad one until later. Keeping the batch identifiable
/// turns "something went wrong on Tuesday" from an hour of hunting into one button.
///
/// Membership lives on <c>collection.import_batch</c> rather than in a list here, so
/// selling or deleting a single card keeps the counts honest for free.
/// </summary>
public sealed class ImportBatchService(Db db, BackupService backups, ILogger<ImportBatchService> log)
{
    /// <summary>Records an import so its cards can be found again. Called once, at commit.</summary>
    public void Create(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO import_batches (id, created_at) VALUES ($id, $createdAt)
            ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Imports that still have cards in the collection, newest first.
    ///
    /// Batches whose every card has since been sold or deleted are left out rather
    /// than lingering as empty rows — there is nothing left to review or undo.
    /// </summary>
    public List<ImportBatchSummary> List() => Query(null);

    public ImportBatchSummary? Get(string id) => Query(id).FirstOrDefault();

    private List<ImportBatchSummary> Query(string? id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.id,
                   b.created_at,
                   b.acknowledged_at,
                   COUNT(c.id),
                   COALESCE(SUM(c.quantity), 0),
                   COALESCE(SUM(CASE WHEN c.modified_at IS NOT NULL THEN 1 ELSE 0 END), 0)
            FROM import_batches b
            LEFT JOIN collection c ON c.import_batch = b.id
            {(id is null ? "" : "WHERE b.id = $id")}
            GROUP BY b.id, b.created_at, b.acknowledged_at
            HAVING COUNT(c.id) > 0
            ORDER BY b.created_at DESC
            """;
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);

        var batches = new List<ImportBatchSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            batches.Add(new ImportBatchSummary(
                Id: r.GetString(0),
                CreatedAt: r.GetString(1),
                AcknowledgedAt: r.IsDBNull(2) ? null : r.GetString(2),
                Entries: r.GetInt32(3),
                Cards: r.GetInt32(4),
                Modified: r.GetInt32(5)));
        return batches;
    }

    /// <summary>Marks an import as checked, which stops its cards being flagged.</summary>
    public bool Acknowledge(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE import_batches SET acknowledged_at = $at WHERE id = $id AND acknowledged_at IS NULL";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Clears the lot, for catching up after several sessions.</summary>
    public int AcknowledgeAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE import_batches SET acknowledged_at = $at WHERE acknowledged_at IS NULL";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes every card still held from an import, and the record of the import.
    ///
    /// Deliberately confined to the <c>collection</c> table. A sale carries its own
    /// copy of the card it was made from and no reference back to the entry, so the
    /// sold ledger and the profit computed from it survive this untouched — and a card
    /// sold outright has already left the collection, so it is not here to remove.
    /// Price history is keyed by card rather than by entry and is shared with
    /// everything else you own, so it is left alone too; it is the one thing in the
    /// database that cannot be fetched again.
    ///
    /// A backup is taken first. This is the most destructive thing the app does on
    /// purpose, and a snapshot costs a fraction of a second.
    /// </summary>
    public (bool Found, int Removed) Remove(string id)
    {
        if (Get(id) is not { } batch) return (false, 0);

        backups.Create("before-import-removal");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        int removed;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM collection WHERE import_batch = $id";
            cmd.Parameters.AddWithValue("$id", id);
            removed = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM import_batches WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();

        log.LogInformation(
            "Removed import {Batch}: {Removed} entries ({Modified} of them edited or partly sold since)",
            id, removed, batch.Modified);

        return (true, removed);
    }
}
