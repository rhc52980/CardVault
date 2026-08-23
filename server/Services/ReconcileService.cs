using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Checks what's in the vault against a fresher read of the scans it came from, and
/// says where the two disagree.
///
/// It changes no card. Scanning improves — OCR gets corrected, a set symbol gets
/// identified, a batch gets re-read — and the CSVs move on while the collection stays
/// as it was imported. That difference is worth knowing about, but acting on it is a
/// judgement about your own cards, so this only ever leaves a note.
///
/// The hard part is knowing which entry a CSV row refers to, because nothing records
/// it. An import batch keeps no filename and its rows carry no identifier, so the link
/// has to be recovered: entries are created in the order the rows were committed, and
/// the CSVs are sorted by file name, so the two run in the same order and can be
/// aligned as sequences. Rows that failed to import show up as gaps, which is exactly
/// what a subsequence alignment is for.
/// </summary>
public sealed class ReconcileService(Db db, ILogger<ReconcileService> log)
{
    /// <summary>One row of a scan CSV, reduced to what identifies a card.</summary>
    private sealed record ScanRow(string File, string? Name, string? SetName, string? Number);

    /// <summary>One entry as it currently stands in the vault.</summary>
    private sealed record Entry(long Id, string? Name, string? SetName, string? Number);

    /// <summary>
    /// Compares every supplied CSV against the import batches and records what
    /// disagrees. Previous flags are cleared first, so a run always describes the
    /// files you just gave it rather than accumulating history.
    /// </summary>
    public ReconcileReport Run(IReadOnlyDictionary<string, string> filesByName, bool apply)
    {
        var scans = new Dictionary<string, List<ScanRow>>();
        foreach (var (name, content) in filesByName)
        {
            var rows = ParseScanCsv(content);
            if (rows.Count > 0) scans[name] = rows;
        }

        var batches = LoadBatches();
        var results = new List<BatchReconcile>();
        var flags = new Dictionary<long, string>();
        var missing = new List<MissingScan>();

        // Longest batch first. A big batch pins down its CSV more reliably than a
        // short one, which might align passably against several.
        var claimed = new HashSet<string>();
        foreach (var (batchId, entries) in batches.OrderByDescending(b => b.Value.Count))
        {
            var unclaimed = scans.Where(s => !claimed.Contains(s.Key)).ToList();
            var best = unclaimed
                .Select(s => (Name: s.Key, Rows: s.Value, Score: Lcs(entries, s.Value).Count))
                .OrderByDescending(s => s.Score)
                .FirstOrDefault();

            // A score of zero means nothing in this batch agrees with anything in the
            // file, which is either a batch whose every card was re-read differently or
            // simply the wrong file. Those look identical from here, so the pairing is
            // only accepted when there's no other file it could be -- guessing would
            // flag a whole batch against a file it never came from.
            if (best.Name is null || (best.Score == 0 && unclaimed.Count > 1))
            {
                results.Add(new BatchReconcile(batchId, null, entries.Count, 0, 0, 0));
                continue;
            }

            claimed.Add(best.Name);
            var anchors = Lcs(entries, best.Rows);
            var disagreements = Pair(entries, best.Rows, anchors);

            foreach (var (entry, scan) in disagreements.Select(d => (d.Entry, d.Row)))
            {
                flags[entry.Id] = scan is null
                    ? "Not found in the latest read of this batch's scans."
                    : $"Scans now read this as {Describe(scan.Name, scan.SetName, scan.Number)}; "
                      + $"the vault has {Describe(entry.Name, entry.SetName, entry.Number)}.";
            }

            // Rows that matched nothing and weren't offered as an explanation for a
            // disagreeing entry either: scanned, but never in the vault at all. Those
            // are cards you physically have and can't see, so they're collected rather
            // than counted — the point is to hand them back as something importable.
            var consumed = anchors.Select(a => a.Row)
                .Concat(disagreements.Where(d => d.RowIndex >= 0).Select(d => d.RowIndex))
                .ToHashSet();

            var leftover = best.Rows
                .Select((row, index) => (row, index))
                .Where(x => !consumed.Contains(x.index))
                .Select(x => new MissingScan(best.Name, x.row.File, x.row.Name, x.row.SetName, x.row.Number))
                .ToList();

            missing.AddRange(leftover);

            results.Add(new BatchReconcile(
                BatchId: batchId,
                ScanFile: best.Name,
                Entries: entries.Count,
                Agreed: anchors.Count,
                Disagreed: disagreements.Count,
                NeverImported: leftover.Count));
        }

        if (apply) ApplyFlags(flags);

        // A file that matched no batch at all is the same story writ large: every row
        // in it was scanned and none of it reached the vault. Left out, the one thing
        // most likely to be entirely missing would be the thing not reported.
        var unmatchedFiles = scans.Keys.Where(k => !claimed.Contains(k)).Order().ToList();
        foreach (var file in unmatchedFiles)
            missing.AddRange(scans[file].Select(r => new MissingScan(file, r.File, r.Name, r.SetName, r.Number)));

        return new ReconcileReport(
            Applied: apply,
            Batches: results.OrderBy(r => r.ScanFile ?? "~").ToList(),
            Agreed: results.Sum(r => r.Agreed),
            Disagreed: results.Sum(r => r.Disagreed),
            NeverImported: missing.Count,
            UnmatchedFiles: unmatchedFiles,
            Missing: missing.OrderBy(m => m.ScanFile).ThenBy(m => m.File).ToList());
    }

    /// <summary>Clears every flag. Nothing else about an entry is touched.</summary>
    public int ClearAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collection SET flagged = NULL, flagged_at = NULL WHERE flagged IS NOT NULL";
        return cmd.ExecuteNonQuery();
    }

    public bool Dismiss(long entryId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collection SET flagged = NULL, flagged_at = NULL WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ------------------------------------------------------------------- alignment

    /// <summary>
    /// The entries and rows that line up, as index pairs, longest run first.
    ///
    /// A plain longest-common-subsequence: both sides are in the same order, and a row
    /// that never imported simply doesn't appear on the entry side. Matching on set and
    /// number rather than name, because a name is what OCR most often gets wrong and
    /// the number is printed in a fixed place.
    /// </summary>
    private static List<(int Entry, int Row)> Lcs(List<Entry> entries, List<ScanRow> rows)
    {
        var n = entries.Count;
        var m = rows.Count;
        var table = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = Same(entries[i], rows[j])
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);

        var pairs = new List<(int, int)>();
        for (int x = 0, y = 0; x < n && y < m;)
        {
            if (Same(entries[x], rows[y])) { pairs.Add((x, y)); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) x++;
            else y++;
        }

        return pairs;
    }

    /// <summary>
    /// Entries that didn't line up, paired with the row that most likely refers to
    /// them: the leftovers between the same two anchors, in order. Beyond those bounds
    /// there's nothing to pair with, so the entry is reported on its own.
    /// </summary>
    private static List<(Entry Entry, ScanRow? Row, int RowIndex)> Pair(
        List<Entry> entries, List<ScanRow> rows, List<(int Entry, int Row)> anchors)
    {
        var paired = new List<(Entry, ScanRow?, int)>();
        var bounds = anchors.Append((Entry: entries.Count, Row: rows.Count)).ToList();

        int e = 0, r = 0;
        foreach (var (anchorEntry, anchorRow) in bounds)
        {
            var spareEntries = Enumerable.Range(e, anchorEntry - e).ToList();
            var spareRows = Enumerable.Range(r, anchorRow - r).ToList();

            for (var k = 0; k < spareEntries.Count; k++)
            {
                var index = k < spareRows.Count ? spareRows[k] : -1;
                paired.Add((entries[spareEntries[k]], index >= 0 ? rows[index] : null, index));
            }

            e = anchorEntry + 1;
            r = anchorRow + 1;
        }

        return paired;
    }

    private static bool Same(Entry entry, ScanRow row)
        => Number(entry.Number) is { Length: > 0 } a
           && Number(row.Number) is { Length: > 0 } b
           && a == b
           && Set(entry.SetName) == Set(row.SetName);

    /// <summary>"006/094" and "6" are the same card — the catalogue keeps the numerator.</summary>
    private static string Number(string? raw)
        => (raw ?? "").Split('/')[0].Trim().TrimStart('0').ToLowerInvariant();

    private static string Set(string? raw)
        => new((raw ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Describe(string? name, string? set, string? number)
    {
        var parts = new[] { name, number, set }.Where(p => !string.IsNullOrWhiteSpace(p));
        return parts.Any() ? string.Join(" · ", parts) : "an unnamed card";
    }

    // -------------------------------------------------------------------- plumbing

    private Dictionary<string, List<Entry>> LoadBatches()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        // Ordered by id, which is creation order, which is the order the rows were
        // committed in. That correspondence is the whole basis of the alignment.
        cmd.CommandText = """
            SELECT c.import_batch, c.id, k.name, k.set_name, k.number
            FROM collection c
            JOIN cards k ON k.id = c.card_id
            WHERE c.import_batch IS NOT NULL
            ORDER BY c.id
            """;

        var batches = new Dictionary<string, List<Entry>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var batch = r.GetString(0);
            if (!batches.TryGetValue(batch, out var list)) batches[batch] = list = [];
            list.Add(new Entry(
                r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return batches;
    }

    private void ApplyFlags(Dictionary<long, string> flags)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE collection SET flagged = NULL, flagged_at = NULL";
            clear.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow.ToString("o");
        foreach (var (id, reason) in flags)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE collection SET flagged = $why, flagged_at = $when WHERE id = $id";
            cmd.Parameters.AddWithValue("$why", reason);
            cmd.Parameters.AddWithValue("$when", now);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        log.LogInformation("Reconciliation flagged {Count} entries for review", flags.Count);
    }

    /// <summary>
    /// Reads a scan CSV. Understands the columns the scanning pipeline writes and the
    /// ones the importer already accepts, so either shape works.
    /// </summary>
    private static List<ScanRow> ParseScanCsv(string content)
    {
        var rows = Csv.Parse(content);
        if (rows.Count < 2) return [];

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace('_', ' ')).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));

        var file = Col("file name", "file", "filename", "image");
        var name = Col("card name", "name");
        var set = Col("set name", "set");
        var number = Col("set number", "number", "card number");

        if (number < 0) return [];

        var parsed = new List<ScanRow>();
        foreach (var row in rows.Skip(1))
        {
            string? At(int i) => i >= 0 && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
            if (At(number) is null) continue;
            parsed.Add(new ScanRow(At(file) ?? "", At(name), At(set), At(number)));
        }

        return parsed;
    }
}
