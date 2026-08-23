using System.Text.Json;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Trading by file rather than by opening a door. Two things must hold: a share file
/// carries no money or whereabouts, and a friend's vault never becomes part of yours.
/// Everything else here is convenience; those two are the promise.
/// </summary>
public sealed class FriendVaultTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly WantsService _wants;
    private readonly FriendVaultService _friends;

    public FriendVaultTests()
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
        _wants = new WantsService(_db, _collection, settings, []);
        _friends = new FriendVaultService(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------ exporting

    /// <summary>
    /// The promise. A file you hand someone must not tell them what you paid, what
    /// it's worth, where you keep it or what you wrote about it.
    /// </summary>
    [Fact]
    public void A_share_file_carries_no_money_and_no_whereabouts()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(
            CardId: "base1-4", Quantity: 2, PurchasePrice: 180, ManualValue: 4000,
            Location: "Safe deposit box", Notes: "the good one"));

        var json = JsonSerializer.Serialize(_friends.Export("My vault"));

        Assert.DoesNotContain("180", json);
        Assert.DoesNotContain("4000", json);
        Assert.DoesNotContain("Safe deposit box", json);
        Assert.DoesNotContain("the good one", json);
        Assert.Contains("Charizard", json);
    }

    [Fact]
    public void An_export_carries_what_you_own_and_what_you_want()
    {
        SeedCard("base1-4", "Charizard");
        SeedCard("base1-58", "Pikachu");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 3));
        _wants.Add(new AddWantRequest("base1-58", TargetPrice: 5));

        var shared = _friends.Export("My vault");

        Assert.Equal(3, Assert.Single(shared.Owned).Quantity);
        Assert.Equal("base1-58", Assert.Single(shared.Wanted).CardId);
    }

    /// <summary>
    /// Sealed product and slabs are synthetic cards whose ids mean nothing outside the
    /// vault that made them. Sending one would be sending a reference to nothing.
    /// </summary>
    [Fact]
    public void Hand_entered_items_are_left_out()
    {
        SeedCard("custom-1", "Booster Box", custom: true);
        _collection.Add(new AddEntryRequest(CardId: "custom-1"));

        Assert.Empty(_friends.Export("My vault").Owned);
    }

    // ------------------------------------------------------------------ importing

    [Fact]
    public void A_friends_vault_is_stored_under_their_name()
    {
        var (ok, error, id) = Import("Dave", ("base1-4", "Charizard", 2));

        Assert.True(ok, error);
        var summary = Assert.Single(_friends.List());
        Assert.Equal(id, summary.Id);
        Assert.Equal("Dave", summary.Name);
        Assert.Equal(2, summary.Owned);
    }

    /// <summary>
    /// The other promise. Importing must not change what you own, what you're worth,
    /// or anything else about your vault.
    /// </summary>
    [Fact]
    public void Importing_does_not_touch_your_own_collection()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));
        var before = _collection.List().Count;

        Import("Dave", ("base1-4", "Charizard", 9), ("zzz-1", "Some Card They Own", 4));

        Assert.Equal(before, _collection.List().Count);
        Assert.Equal(1, _collection.Stats().TotalCards);
    }

    [Fact]
    public void Removing_a_friends_vault_takes_all_of_it()
    {
        var (_, _, id) = Import("Dave", ("base1-4", "Charizard", 2));

        Assert.True(_friends.Delete(id));
        Assert.Empty(_friends.List());
        Assert.Empty(_friends.Cards(id, "own"));
    }

    [Fact]
    public void Two_friends_vaults_stay_apart()
    {
        Import("Dave", ("base1-4", "Charizard", 1));
        Import("Sam", ("base1-58", "Pikachu", 1));

        Assert.Equal(2, _friends.List().Count);
        Assert.Equal(["Dave", "Sam"], _friends.List().Select(v => v.Name).Order());
    }

    [Fact]
    public void A_file_that_is_not_a_vault_is_refused()
    {
        var (ok, error, _) = _friends.Import("this is not json", null);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_refused_rather_than_half_read()
    {
        var json = JsonSerializer.Serialize(new SharedVault(99, "Future", null, [], []));

        var (ok, error, _) = _friends.Import(json, null);

        Assert.False(ok);
        Assert.Contains("newer CardVault", error);
    }

    // ------------------------------------------------------------------- matching

    [Fact]
    public void Cards_they_own_that_you_want_are_matched()
    {
        SeedCard("base1-4", "Charizard");
        _wants.Add(new AddWantRequest("base1-4"));
        var (_, _, id) = Import("Dave", ("base1-4", "Charizard", 3));

        var match = Assert.Single(_friends.Matches(id)!.TheyHave);
        Assert.Equal("Charizard", match.Name);
        Assert.Equal(3, match.TheirQuantity);
    }

    /// <summary>
    /// A single copy is the one in your binder. Offering it is a decision, not an
    /// inventory fact, so only genuine spares are listed.
    /// </summary>
    [Fact]
    public void Only_spares_are_offered()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 1));
        var (_, _, id) = ImportWants("Dave", ("base1-4", "Charizard", 1));

        Assert.Empty(_friends.Matches(id)!.YouCouldOffer);
    }

    [Fact]
    public void A_spare_is_what_you_hold_beyond_the_one_you_keep()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 3));
        var (_, _, id) = ImportWants("Dave", ("base1-4", "Charizard", 1));

        Assert.Equal(2, Assert.Single(_friends.Matches(id)!.YouCouldOffer).YourQuantity);
    }

    [Fact]
    public void A_vault_that_matches_nothing_says_so_rather_than_failing()
    {
        SeedCard("base1-4", "Charizard");
        var (_, _, id) = Import("Dave", ("base1-4", "Charizard", 1));

        var matches = _friends.Matches(id)!;
        Assert.Empty(matches.TheyHave);
        Assert.Empty(matches.YouCouldOffer);
    }

    // ------------------------------------------------------------------- fixtures

    private (bool Ok, string? Error, long Id) Import(string name, params (string Id, string Name, int Qty)[] owned)
        => _friends.Import(JsonSerializer.Serialize(new SharedVault(
            1, name, null,
            [.. owned.Select(c => new SharedCard(c.Id, c.Name, "Base", "4", null, "NM", "en", c.Qty))],
            [])), null);

    private (bool Ok, string? Error, long Id) ImportWants(string name, params (string Id, string Name, int Qty)[] wanted)
        => _friends.Import(JsonSerializer.Serialize(new SharedVault(
            1, name, null, [],
            [.. wanted.Select(c => new SharedCard(c.Id, c.Name, "Base", "4", null, null, null, c.Qty))])), null);

    private void SeedCard(string cardId, string name, bool custom = false)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = cardId,
            ["name"] = name,
        });

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, set_name, number, payload, cached_at, is_custom)
            VALUES ($id, $name, 'Base', '4', $payload, $now, $custom)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$custom", custom ? 1 : 0);
        cmd.ExecuteNonQuery();
    }
}
