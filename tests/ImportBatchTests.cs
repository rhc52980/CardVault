using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Removing an import is the most destructive thing the app does deliberately, and
/// the whole reason it can be offered at all is that a sale keeps its own copy of the
/// card it was made from. These pin that down: if a future change makes sales point
/// back at collection rows instead, the test that fails here is the one standing
/// between a bad batch and someone's profit history.
///
/// Runs against a real SQLite file in a temp directory rather than a mock, because
/// what's being checked is what the DELETE actually touches.
/// </summary>
public sealed class ImportBatchTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly ImportBatchService _batches;

    public ImportBatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardvault-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);

        // An empty database file up front so DataPaths' legacy migration sees a
        // collection already present and leaves the real one on this machine alone.
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
        _batches = new ImportBatchService(
            _db, new BackupService(paths, _db, NullLogger<BackupService>.Instance),
            NullLogger<ImportBatchService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------- fixtures

    private void SeedCard(string cardId, string name)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at)
            VALUES ($id, $name, '1', $payload, $now)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"{{name}}"}""");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void SeedPriceHistory(string cardId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, 'normal', 'tcgplayer', 'USD', '2026-08-01', 12.5)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long AddTo(string batch, string cardId)
    {
        _batches.Create(batch);
        return _collection.Add(new AddEntryRequest(CardId: cardId), importBatch: batch);
    }

    // --------------------------------------------------------------------- counts

    [Fact]
    public void A_batch_reports_entries_cards_and_nothing_modified()
    {
        SeedCard("base1-4", "Charizard");
        _batches.Create("job1");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 3), importBatch: "job1");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 1), importBatch: "job1");

        var batch = _batches.Get("job1");

        Assert.NotNull(batch);
        Assert.Equal(2, batch.Entries);
        Assert.Equal(4, batch.Cards);
        Assert.Equal(0, batch.Modified);
        Assert.Null(batch.AcknowledgedAt);
    }

    [Fact]
    public void Editing_an_entry_counts_it_as_modified()
    {
        SeedCard("base1-4", "Charizard");
        var id = AddTo("job1", "base1-4");

        _collection.Update(id, new UpdateEntryRequest(Condition: "LP"));

        Assert.Equal(1, _batches.Get("job1")!.Modified);
    }

    [Fact]
    public void A_batch_with_nothing_left_in_it_stops_being_listed()
    {
        // Every card sold or deleted means there is nothing to review or undo.
        SeedCard("base1-4", "Charizard");
        var id = AddTo("job1", "base1-4");

        Assert.Single(_batches.List());
        _collection.Delete(id);
        Assert.Empty(_batches.List());
    }

    [Fact]
    public void Cards_added_by_hand_belong_to_no_batch()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        Assert.Empty(_batches.List());
        Assert.All(_collection.List(), i => Assert.Null(i.ImportBatch));
    }

    // -------------------------------------------------------------- acknowledging

    [Fact]
    public void Acknowledging_marks_the_batch_and_does_not_move_the_cards()
    {
        SeedCard("base1-4", "Charizard");
        AddTo("job1", "base1-4");

        Assert.True(_batches.Acknowledge("job1"));
        Assert.NotNull(_batches.Get("job1")!.AcknowledgedAt);
        Assert.Single(_collection.List());

        // Already acknowledged — nothing further to do, and the timestamp stands.
        Assert.False(_batches.Acknowledge("job1"));
    }

    // -------------------------------------------------------------------- removal

    [Fact]
    public void Removing_a_batch_takes_only_its_own_cards()
    {
        SeedCard("base1-4", "Charizard");
        SeedCard("base1-2", "Blastoise");
        AddTo("job1", "base1-4");
        AddTo("job2", "base1-2");
        _collection.Add(new AddEntryRequest(CardId: "base1-4")); // added by hand

        var (found, removed) = _batches.Remove("job1");

        Assert.True(found);
        Assert.Equal(1, removed);

        var left = _collection.List();
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, i => i.ImportBatch == "job1");
        Assert.Contains(left, i => i.ImportBatch == "job2");
        Assert.Contains(left, i => i.ImportBatch is null);
    }

    [Fact]
    public void Removing_a_batch_leaves_the_sold_ledger_untouched()
    {
        SeedCard("base1-4", "Charizard");
        var id = AddTo("job1", "base1-4");

        var sales = new SalesService(_db, _collection);
        var (ok, error, _) = sales.Sell(id, new SellRequest(SalePrice: 400, Quantity: 1));
        Assert.True(ok, error);

        // Selling the only copy already removed the entry, so the batch is empty.
        Assert.Single(sales.List());

        _batches.Remove("job1");

        // The sale survives with its own copy of the card intact — this is the whole
        // reason removing an import can be offered at all.
        var remaining = sales.List();
        Assert.Single(remaining);
        Assert.Equal("Charizard", remaining[0].CardName);
        Assert.Equal(400, remaining[0].SalePrice);
    }

    [Fact]
    public void Removing_a_batch_leaves_price_history_untouched()
    {
        // Price history is keyed by card rather than by entry and is shared with
        // everything else you own. It is also the one thing here that cannot be
        // fetched again, so a batch removal must not go near it.
        SeedCard("base1-4", "Charizard");
        SeedPriceHistory("base1-4");
        AddTo("job1", "base1-4");

        var before = Count("SELECT COUNT(*) FROM price_history");
        _batches.Remove("job1");

        Assert.Equal(before, Count("SELECT COUNT(*) FROM price_history"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM cards"));
    }

    [Fact]
    public void Removing_an_unknown_batch_changes_nothing()
    {
        SeedCard("base1-4", "Charizard");
        AddTo("job1", "base1-4");

        var (found, removed) = _batches.Remove("never-existed");

        Assert.False(found);
        Assert.Equal(0, removed);
        Assert.Single(_collection.List());
    }
}
