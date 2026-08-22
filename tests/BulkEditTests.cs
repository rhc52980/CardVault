using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Acting on a selection is the one place a mistake is expensive: getting it wrong on
/// one card is an edit, getting it wrong on two thousand is an afternoon. These pin
/// down that a bulk edit does exactly what the single-card one does, to exactly the
/// rows named and no others.
/// </summary>
public sealed class BulkEditTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;

    public BulkEditTests()
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
        SeedCard("base1-4");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // -------------------------------------------------------------------- editing

    [Fact]
    public void An_edit_reaches_every_entry_named()
    {
        var ids = AddMany(3);

        var changed = _collection.UpdateMany(ids, new UpdateEntryRequest(Location: "Box A"));

        Assert.Equal(3, changed);
        Assert.All(_collection.List(), i => Assert.Equal("Box A", i.Location));
    }

    /// <summary>The whole risk of a bulk edit: touching something you didn't select.</summary>
    [Fact]
    public void An_edit_reaches_nothing_else()
    {
        var ids = AddMany(3);
        var untouched = _collection.Add(new AddEntryRequest(CardId: "base1-4", Location: "Safe"));

        _collection.UpdateMany([ids[0], ids[1]], new UpdateEntryRequest(Location: "Box A"));

        Assert.Equal("Safe", _collection.List().Single(i => i.Id == untouched).Location);
    }

    /// <summary>
    /// The bulk path builds its SET clause with the same code as the single one, so
    /// normalisation can't hold for one card and not for fifty.
    /// </summary>
    [Fact]
    public void A_language_is_normalised_the_same_way_in_bulk()
    {
        var ids = AddMany(2);

        _collection.UpdateMany(ids, new UpdateEntryRequest(Language: "Japanese"));

        Assert.All(_collection.List(), i => Assert.Equal("ja", i.Language));
    }

    [Fact]
    public void Clearing_a_value_works_in_bulk_too()
    {
        var ids = new[]
        {
            _collection.Add(new AddEntryRequest(CardId: "base1-4", ManualValue: 10)),
            _collection.Add(new AddEntryRequest(CardId: "base1-4", ManualValue: 20)),
        };

        _collection.UpdateMany(ids, new UpdateEntryRequest(ClearManualValue: true));

        Assert.All(_collection.List(), i => Assert.Null(i.ManualValue));
    }

    [Fact]
    public void An_empty_selection_changes_nothing()
    {
        AddMany(2);
        Assert.Equal(0, _collection.UpdateMany([], new UpdateEntryRequest(Location: "Box A")));
        Assert.All(_collection.List(), i => Assert.Null(i.Location));
    }

    /// <summary>
    /// A request that asks for nothing is not an error, but it must not stamp
    /// modified_at either — that flag is what tells you an imported card has been
    /// touched, and a no-op would make every card look edited.
    /// </summary>
    [Fact]
    public void A_request_with_no_fields_changes_nothing()
    {
        var ids = AddMany(2);
        Assert.Equal(0, _collection.UpdateMany(ids, new UpdateEntryRequest()));
    }

    [Fact]
    public void An_id_that_does_not_exist_is_simply_not_found()
    {
        var ids = AddMany(2);

        var changed = _collection.UpdateMany([.. ids, 9999], new UpdateEntryRequest(Location: "Box A"));

        Assert.Equal(2, changed);
    }

    [Fact]
    public void The_same_id_twice_is_counted_once()
    {
        var ids = AddMany(1);
        Assert.Equal(1, _collection.UpdateMany([ids[0], ids[0]], new UpdateEntryRequest(Location: "Box A")));
    }

    // ------------------------------------------------------------------- removing

    [Fact]
    public void Removing_takes_the_selection_and_leaves_the_rest()
    {
        var ids = AddMany(4);

        var removed = _collection.DeleteMany([ids[0], ids[2]]);

        Assert.Equal(2, removed);
        Assert.Equal([ids[1], ids[3]], _collection.List().Select(i => i.Id).Order());
    }

    [Fact]
    public void Removing_nothing_removes_nothing()
    {
        AddMany(2);
        Assert.Equal(0, _collection.DeleteMany([]));
        Assert.Equal(2, _collection.List().Count);
    }

    /// <summary>
    /// A selection can be the whole vault, which is more parameters than SQLite will
    /// take in one statement. Chunking is what stops that being an error, so it needs
    /// a case that actually crosses the boundary.
    /// </summary>
    [Fact]
    public void A_selection_larger_than_one_statement_still_works()
    {
        var ids = AddMany(950);

        var changed = _collection.UpdateMany(ids, new UpdateEntryRequest(Location: "Box A"));

        Assert.Equal(950, changed);
        Assert.Equal(950, _collection.DeleteMany(ids));
        Assert.Empty(_collection.List());
    }

    // ------------------------------------------------------------------ fixtures

    private long[] AddMany(int n) =>
        [.. Enumerable.Range(0, n).Select(_ => _collection.Add(new AddEntryRequest(CardId: "base1-4")))];

    private void SeedCard(string cardId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at)
            VALUES ($id, 'Charizard', '4', $payload, $now)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"Charizard"}""");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
