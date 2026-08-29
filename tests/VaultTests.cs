using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Several collections in one installation, for a household rather than for
/// strangers: one password, then you pick whose cards you're looking at.
///
/// The promise is separation, not secrecy. These pin down that two collections never
/// see each other's cards, that the first one keeps its files exactly where a
/// single-vault install already put them, and that the first one can't be deleted.
/// </summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _dir;
    private readonly DataPaths _paths;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly VaultRegistry _vaults;

    public VaultTests()
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

        _paths = new DataPaths(config, NullLogger<DataPaths>.Instance);
        _db = new Db(_paths);
        _db.Initialize();

        var settings = new SettingsService(_db, config);
        _collection = new CollectionService(_db, settings, []);
        _vaults = new VaultRegistry(_paths, NullLogger<VaultRegistry>.Instance);
    }

    public void Dispose()
    {
        CurrentVault.Id = CurrentVault.Default;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // -------------------------------------------------------------- the registry

    [Fact]
    public void A_fresh_install_has_exactly_one_collection()
    {
        var vault = Assert.Single(_vaults.List());
        Assert.Equal(CurrentVault.Default, vault.Id);
        Assert.True(vault.IsDefault);
    }

    [Fact]
    public void A_new_collection_appears_alongside_the_first()
    {
        var (ok, error, id) = _vaults.Create("Sam's cards");

        Assert.True(ok, error);
        Assert.Equal("sam-s-cards", id);
        Assert.Equal(2, _vaults.List().Count);
        Assert.False(_vaults.List().Single(v => v.Id == id).IsDefault);
    }

    /// <summary>A name becomes a folder, so anything that isn't safe in a path goes.</summary>
    [Theory]
    [InlineData("Sam's cards", "sam-s-cards")]
    [InlineData("  Dave  ", "dave")]
    [InlineData("../../etc", "etc")]
    [InlineData("A / B", "a-b")]
    public void A_name_becomes_a_safe_folder(string name, string expected)
    {
        var (ok, _, id) = _vaults.Create(name);
        Assert.True(ok);
        Assert.Equal(expected, id);
    }

    [Fact]
    public void Two_collections_cannot_share_a_name()
    {
        _vaults.Create("Dave");
        var (ok, error, _) = _vaults.Create("dave");

        Assert.False(ok);
        Assert.Contains("already", error);
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_is_refused()
    {
        var (ok, error, _) = _vaults.Create("!!!");

        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ------------------------------------------------------------------ isolation

    /// <summary>
    /// The whole point. Two collections in one install must not see each other.
    /// </summary>
    [Fact]
    public void Cards_in_one_collection_are_invisible_from_the_other()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 3));
        Assert.Single(_collection.List());

        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;

        Assert.Empty(_collection.List());
        Assert.Equal(0, _collection.Stats().TotalCards);
    }

    [Fact]
    public void Adding_to_one_collection_leaves_the_other_alone()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;
        SeedCard("base1-58", "Pikachu");
        _collection.Add(new AddEntryRequest(CardId: "base1-58", Quantity: 9));

        Assert.Equal(9, _collection.Stats().TotalCards);

        CurrentVault.Id = CurrentVault.Default;
        Assert.Equal(1, _collection.Stats().TotalCards);
        Assert.Equal("Charizard", Assert.Single(_collection.List()).Name);
    }

    /// <summary>
    /// Nothing about a single-vault install moves. Its database stays at the root,
    /// which is what makes gaining this feature a no-op for an existing collection.
    /// </summary>
    [Fact]
    public void The_first_collection_keeps_its_files_where_they_were()
    {
        Assert.Equal(Path.Combine(_dir, "vault.db"), _paths.DatabaseFile);
        Assert.Equal(_dir, _paths.VaultRoot);
    }

    [Fact]
    public void A_second_collection_gets_a_folder_of_its_own()
    {
        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;

        Assert.Equal(Path.Combine(_dir, "vaults", "sam"), _paths.VaultRoot);
        Assert.Equal(Path.Combine(_dir, "vaults", "sam", "vault.db"), _paths.DatabaseFile);
    }

    /// <summary>
    /// Card art is the same public image whatever collection asked for it, so two
    /// people in one house shouldn't download it twice into separate folders.
    /// </summary>
    [Fact]
    public void Card_artwork_is_shared_between_collections()
    {
        var shared = _paths.ImagesDirectory;

        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;

        Assert.Equal(shared, _paths.ImagesDirectory);
    }

    [Fact]
    public void Photos_and_backups_are_not_shared()
    {
        var mine = (_paths.PhotosDirectory, _paths.BackupsDirectory);

        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;

        Assert.NotEqual(mine.PhotosDirectory, _paths.PhotosDirectory);
        Assert.NotEqual(mine.BackupsDirectory, _paths.BackupsDirectory);
    }

    // ------------------------------------------------------------------- removing

    /// <summary>
    /// The collection an install starts with is the one it was set up for. A stray
    /// click there would take the cards the whole thing exists to hold.
    /// </summary>
    [Fact]
    public void The_first_collection_cannot_be_removed()
    {
        var (ok, error) = _vaults.Delete(CurrentVault.Default);

        Assert.False(ok);
        Assert.Contains("can't be removed", error);
        Assert.Single(_vaults.List());
    }

    [Fact]
    public void Removing_a_collection_takes_its_files_with_it()
    {
        var (_, _, id) = _vaults.Create("Sam");
        CurrentVault.Id = id!;
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));
        var dir = _paths.VaultRoot;
        Assert.True(Directory.Exists(dir));

        CurrentVault.Id = CurrentVault.Default;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (ok, _) = _vaults.Delete(id!);

        Assert.True(ok);
        Assert.Single(_vaults.List());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Removing_something_that_is_not_there_says_so()
    {
        var (ok, error) = _vaults.Delete("nobody");

        Assert.False(ok);
        Assert.Contains("No such", error);
    }

    // -------------------------------------------------------------------- naming

    [Fact]
    public void A_collection_can_be_renamed()
    {
        var (_, _, id) = _vaults.Create("Sam");

        Assert.True(_vaults.Rename(id!, "Sam's Pokemon"));
        Assert.Equal("Sam's Pokemon", _vaults.List().Single(v => v.Id == id).Name);
    }

    /// <summary>Renaming changes the label, never the folder — that would strand the files.</summary>
    [Fact]
    public void Renaming_does_not_move_anything()
    {
        var (_, _, id) = _vaults.Create("Sam");
        _vaults.Rename(id!, "Something Else Entirely");

        Assert.Equal(id, _vaults.List().Single(v => !v.IsDefault).Id);
    }

    [Fact]
    public void The_first_collection_can_be_renamed_too()
    {
        Assert.True(_vaults.Rename(CurrentVault.Default, "Rob's cards"));
        Assert.Equal("Rob's cards", _vaults.List().Single(v => v.IsDefault).Name);
    }

    // ------------------------------------------------------------------ fixtures

    private void SeedCard(string cardId, string name)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO cards (id, name, set_name, number, payload, cached_at)
            VALUES ($id, $name, 'Base', '4', $payload, $now)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"{{name}}"}""");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
