using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Reconciliation reads a fresher scan of cards you already own and says where it
/// disagrees. The one thing it must never do is act on that: these pin down that a
/// flagged entry keeps its card, its set, its number and its quantity, and that the
/// alignment survives rows which never imported — which is most of the reason the
/// two sides drift apart in the first place.
/// </summary>
public sealed class ReconcileTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly ReconcileService _reconcile;

    public ReconcileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardvault-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "vault.db"), []);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CardVault:DataDirectory"] = _dir,
            })
            .Build();

        var paths = new DataPaths(config, NullLogger<DataPaths>.Instance);
        _db = new Db(paths);
        _db.Initialize();

        var settings = new SettingsService(_db, config);
        _collection = new CollectionService(_db, settings, []);
        _reconcile = new ReconcileService(_db, NullLogger<ReconcileService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private const string Header = "File_Name,Card_Name,Set_Name,Set_Number\n";

    // ------------------------------------------------------------------ agreement

    [Fact]
    public void A_batch_that_matches_its_file_flags_nothing()
    {
        Import("job1", ("Pikachu", "Base", "58"), ("Charizard", "Base", "4"));
        var csv = Header + "a.png,Pikachu,Base,058/102\nb.png,Charizard,Base,004/102\n";

        var report = _reconcile.Run(new Dictionary<string, string> { ["Batch_1.csv"] = csv }, apply: true);

        Assert.Equal(2, report.Agreed);
        Assert.Equal(0, report.Disagreed);
        Assert.All(_collection.List(), i => Assert.Null(i.Flagged));
    }

    /// <summary>"004/102" and "4" are the same card; only the numerator is stored.</summary>
    [Fact]
    public void A_printed_number_matches_the_stored_one()
    {
        Import("job1", ("Charizard", "Base", "4"));
        var report = Run("Batch_1.csv", Header + "a.png,Charizard,Base,004/102\n");

        Assert.Equal(1, report.Agreed);
    }

    // --------------------------------------------------------------- disagreement

    [Fact]
    public void A_card_the_scans_now_read_differently_is_flagged()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));
        Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\n", apply: true);

        var flagged = Assert.Single(_collection.List());
        Assert.NotNull(flagged.Flagged);
        Assert.Contains("Darkness Ablaze", flagged.Flagged);
        Assert.Contains("Chilling Reign", flagged.Flagged);
    }

    /// <summary>
    /// The whole promise of the feature. A flag is a note beside the card, not an
    /// edit to it — if this ever fails, the feature is doing the one thing it said
    /// it would never do.
    /// </summary>
    [Fact]
    public void Flagging_changes_nothing_about_the_card()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));
        var before = Assert.Single(_collection.List());

        Run("Batch_1.csv", Header + "a.png,Something Else,Darkness Ablaze,153/189\n", apply: true);

        var after = Assert.Single(_collection.List());
        Assert.Equal(before.CardId, after.CardId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.SetName, after.SetName);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.Quantity, after.Quantity);
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));

        var report = Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\n");

        Assert.False(report.Applied);
        Assert.Equal(1, report.Disagreed);
        Assert.Null(Assert.Single(_collection.List()).Flagged);
    }

    // ------------------------------------------------------------------ alignment

    /// <summary>
    /// Rows that failed to import are the normal case, not the exception — they shift
    /// every position after them, which is why this aligns rather than counting.
    /// </summary>
    [Fact]
    public void Rows_that_never_imported_do_not_shift_everything_after_them()
    {
        Import("job1", ("Pikachu", "Base", "58"), ("Charizard", "Base", "4"));
        var csv = Header
                  + "a.png,Pikachu,Base,058/102\n"
                  + "b.png,Mystery,Base,999/102\n"   // never imported
                  + "c.png,Charizard,Base,004/102\n";

        var report = Run("Batch_1.csv", csv, apply: true);

        Assert.Equal(2, report.Agreed);
        Assert.Equal(0, report.Disagreed);
        Assert.Equal(1, report.NeverImported);
        Assert.All(_collection.List(), i => Assert.Null(i.Flagged));
    }

    [Fact]
    public void The_never_imported_count_cannot_go_negative()
    {
        Import("job1", ("Pikachu", "Base", "58"), ("Charizard", "Base", "4"), ("Blastoise", "Base", "2"));
        var report = Run("Batch_1.csv", Header + "a.png,Pikachu,Base,058/102\n");

        Assert.True(report.NeverImported >= 0, $"was {report.NeverImported}");
    }

    [Fact]
    public void Each_file_is_claimed_by_at_most_one_batch()
    {
        Import("job1", ("Pikachu", "Base", "58"));
        Import("job2", ("Pikachu", "Base", "58"));

        var csv = Header + "a.png,Pikachu,Base,058/102\n";
        var report = _reconcile.Run(new Dictionary<string, string> { ["Batch_1.csv"] = csv }, apply: false);

        Assert.Single(report.Batches, b => b.ScanFile is not null);
    }

    [Fact]
    public void A_file_matching_no_batch_is_reported_rather_than_ignored()
    {
        Import("job1", ("Pikachu", "Base", "58"));
        var report = _reconcile.Run(new Dictionary<string, string>
        {
            ["Batch_1.csv"] = Header + "a.png,Pikachu,Base,058/102\n",
            ["Batch_2.csv"] = Header + "b.png,Snorlax,Jungle,011/064\n",
        }, apply: false);

        Assert.Equal("Batch_2.csv", Assert.Single(report.UnmatchedFiles));
    }

    // -------------------------------------------------------- scanned but missing

    /// <summary>
    /// A row that never imported is a card you physically own and cannot see. Counting
    /// it is no use; the point is to hand it back in a shape the importer will take.
    /// </summary>
    [Fact]
    public void A_row_that_never_imported_is_returned_not_just_counted()
    {
        Import("job1", ("Pikachu", "Base", "58"));
        var csv = Header
                  + "a.png,Pikachu,Base,058/102\n"
                  + "b.png,Snorlax,Jungle,011/064\n";

        var report = Run("Batch_1.csv", csv);

        var missing = Assert.Single(report.Missing);
        Assert.Equal("Snorlax", missing.Name);
        Assert.Equal("Jungle", missing.SetName);
        Assert.Equal("011/064", missing.Number);
        Assert.Equal("b.png", missing.File);
        Assert.Equal("Batch_1.csv", missing.ScanFile);
    }

    /// <summary>
    /// A whole file that matched nothing is the case most likely to be entirely
    /// missing, so leaving its rows out would omit exactly what matters most.
    /// </summary>
    [Fact]
    public void Every_row_of_an_unmatched_file_counts_as_missing()
    {
        Import("job1", ("Pikachu", "Base", "58"));
        var report = _reconcile.Run(new Dictionary<string, string>
        {
            ["Batch_1.csv"] = Header + "a.png,Pikachu,Base,058/102\n",
            ["Batch_2.csv"] = Header + "b.png,Snorlax,Jungle,011/064\nc.png,Gyarados,Base,006/102\n",
        }, apply: false);

        Assert.Equal(2, report.Missing.Count);
        Assert.All(report.Missing, m => Assert.Equal("Batch_2.csv", m.ScanFile));
        Assert.Equal(2, report.NeverImported);
    }

    /// <summary>
    /// A row offered as the explanation for a disagreeing entry has been accounted
    /// for. Reporting it as missing too would have you import a card you already own.
    /// </summary>
    [Fact]
    public void A_row_explaining_a_disagreement_is_not_also_missing()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));
        var report = Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\n");

        Assert.Equal(1, report.Disagreed);
        Assert.Empty(report.Missing);
        Assert.Equal(0, report.NeverImported);
    }

    [Fact]
    public void A_file_that_agrees_completely_reports_nothing_missing()
    {
        Import("job1", ("Pikachu", "Base", "58"));
        var report = Run("Batch_1.csv", Header + "a.png,Pikachu,Base,058/102\n");

        Assert.Empty(report.Missing);
    }

    // ------------------------------------------------------------------ dismissing

    [Fact]
    public void A_flag_can_be_dismissed_one_card_at_a_time()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));
        Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\n", apply: true);

        var id = _collection.List().Single().Id;
        Assert.True(_reconcile.Dismiss(id));
        Assert.Null(Assert.Single(_collection.List()).Flagged);
    }

    [Fact]
    public void Clearing_removes_every_flag()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"), ("Furfrou", "Kalos Starter Set", "33"));
        Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\nb.png,Furfrou,Fates Collide,020/030\n",
            apply: true);

        Assert.Equal(2, _reconcile.ClearAll());
        Assert.All(_collection.List(), i => Assert.Null(i.Flagged));
    }

    /// <summary>
    /// A run describes the files you just gave it. Without clearing first, a card
    /// fixed since the last run would keep a flag that no longer refers to anything.
    /// </summary>
    [Fact]
    public void A_second_run_replaces_the_first_rather_than_adding_to_it()
    {
        Import("job1", ("Greedent", "Chilling Reign", "128"));
        Run("Batch_1.csv", Header + "a.png,Greedent,Darkness Ablaze,153/189\n", apply: true);
        Assert.NotNull(_collection.List().Single().Flagged);

        Run("Batch_1.csv", Header + "a.png,Greedent,Chilling Reign,128/198\n", apply: true);

        Assert.Null(_collection.List().Single().Flagged);
    }

    // -------------------------------------------------------------------- fixtures

    private ReconcileReport Run(string file, string csv, bool apply = false)
        => _reconcile.Run(new Dictionary<string, string> { [file] = csv }, apply);

    private void Import(string batch, params (string Name, string Set, string Number)[] cards)
    {
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO import_batches (id, created_at) VALUES ($id, $now)";
            cmd.Parameters.AddWithValue("$id", batch);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        foreach (var (name, set, number) in cards)
        {
            var cardId = $"{batch}-{name}-{number}".ToLowerInvariant();
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO cards (id, name, set_name, number, payload, cached_at)
                    VALUES ($id, $name, $set, $number, $payload, $now)
                    """;
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$set", set);
                cmd.Parameters.AddWithValue("$number", number);
                cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"{{name}}"}""");
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            _collection.Add(new AddEntryRequest(CardId: cardId), importBatch: batch);
        }
    }
}
