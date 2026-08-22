using System.Text;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Photos are the only feature that writes files someone can't get back. These pin
/// down that it stays off until asked, that turning it off never deletes anything,
/// that deleting is a separate and deliberate act, and that a file can't outlive the
/// card it was taken of.
/// </summary>
public sealed class PhotoTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly DataPaths _paths;
    private readonly CollectionService _collection;
    private readonly PhotoService _photos;

    public PhotoTests()
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
        _photos = new PhotoService(_db, _paths, settings, NullLogger<PhotoService>.Instance);
        SeedCard("base1-4");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------ switched off

    /// <summary>An unused feature should leave nothing behind, folder included.</summary>
    [Fact]
    public void It_is_off_and_has_no_folder_until_switched_on()
    {
        Assert.False(_photos.Enabled);
        Assert.False(Directory.Exists(_paths.PhotosDirectory));
    }

    [Fact]
    public async Task Nothing_can_be_attached_while_it_is_off()
    {
        var id = AddEntry();

        var (ok, error) = await Attach(id, "card.png");

        Assert.False(ok);
        Assert.Contains("switched off", error);
        Assert.False(Directory.Exists(_paths.PhotosDirectory));
    }

    // ------------------------------------------------------------------- attaching

    [Fact]
    public async Task A_photo_attaches_and_the_entry_says_so()
    {
        _photos.SetEnabled(true);
        var id = AddEntry();

        var (ok, error) = await Attach(id, "card.png");

        Assert.True(ok, error);
        Assert.True(Single().HasPhoto);
        Assert.NotNull(_photos.Resolve(id));
    }

    /// <summary>
    /// Two copies of one card is the whole reason this hangs off the entry. If it
    /// were keyed by card they would share an image and the feature would be pointless.
    /// </summary>
    [Fact]
    public async Task Two_copies_of_one_card_carry_different_photos()
    {
        _photos.SetEnabled(true);
        var first = AddEntry();
        var second = AddEntry();

        await Attach(first, "one.png", "first");
        await Attach(second, "two.png", "second");

        Assert.Equal("first", File.ReadAllText(_photos.Resolve(first)!.Value.Path));
        Assert.Equal("second", File.ReadAllText(_photos.Resolve(second)!.Value.Path));
    }

    [Theory]
    [InlineData("card.gif")]
    [InlineData("card.pdf")]
    [InlineData("card")]
    public async Task Only_types_a_browser_will_render_are_accepted(string name)
    {
        _photos.SetEnabled(true);
        var id = AddEntry();

        var (ok, error) = await Attach(id, name);

        Assert.False(ok);
        Assert.Contains("PNG", error);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        _photos.SetEnabled(true);
        var id = AddEntry();

        var (ok, _) = await _photos.AttachAsync(id, new MemoryStream(), "card.png", default);

        Assert.False(ok);
        Assert.False(Single().HasPhoto);
    }

    /// <summary>
    /// A replacement with a different extension writes a different filename, so the
    /// old file has to go explicitly or it sits in the folder forever, counted
    /// against your disk usage and belonging to nothing.
    /// </summary>
    [Fact]
    public async Task Replacing_a_photo_leaves_no_orphan_behind()
    {
        _photos.SetEnabled(true);
        var id = AddEntry();

        await Attach(id, "card.png");
        await Attach(id, "card.jpg", "replacement");

        Assert.Single(Directory.EnumerateFiles(_paths.PhotosDirectory));
        Assert.Equal("replacement", File.ReadAllText(_photos.Resolve(id)!.Value.Path));
    }

    // -------------------------------------------------------------------- removing

    [Fact]
    public async Task Detaching_removes_the_file_and_the_flag()
    {
        _photos.SetEnabled(true);
        var id = AddEntry();
        await Attach(id, "card.png");

        Assert.True(_photos.Detach(id));

        Assert.False(Single().HasPhoto);
        Assert.Null(_photos.Resolve(id));
        Assert.Empty(Directory.EnumerateFiles(_paths.PhotosDirectory));
    }

    /// <summary>
    /// The important one. Someone switching the feature off to tidy their settings
    /// must not lose files by it — that is what the delete button is for, and it says
    /// what it does.
    /// </summary>
    [Fact]
    public async Task Switching_it_off_keeps_every_photo()
    {
        _photos.SetEnabled(true);
        var id = AddEntry();
        await Attach(id, "card.png");

        _photos.SetEnabled(false);

        Assert.Single(Directory.EnumerateFiles(_paths.PhotosDirectory));
        Assert.NotNull(_photos.Resolve(id));

        // And switching back on finds it still there.
        _photos.SetEnabled(true);
        Assert.True(Single().HasPhoto);
    }

    [Fact]
    public async Task Deleting_them_all_takes_the_files_and_the_flags()
    {
        _photos.SetEnabled(true);
        var first = AddEntry();
        var second = AddEntry();
        await Attach(first, "one.png");
        await Attach(second, "two.png");

        Assert.Equal(2, _photos.DeleteAll());

        Assert.False(Directory.Exists(_paths.PhotosDirectory));
        Assert.All(_collection.List(), i => Assert.False(i.HasPhoto));
    }

    // -------------------------------------------------------------------- orphans

    /// <summary>
    /// An entry can vanish several ways — sold, removed, or swept up by undoing an
    /// import — so the file is reclaimed by a sweep rather than by remembering to
    /// delete it from each of those paths.
    /// </summary>
    [Fact]
    public async Task A_photo_does_not_outlive_the_card_it_was_taken_of()
    {
        _photos.SetEnabled(true);
        var kept = AddEntry();
        var sold = AddEntry();
        await Attach(kept, "keep.png");
        await Attach(sold, "gone.png");

        _collection.Delete(sold);

        Assert.Equal(1, _photos.CleanUpOrphans());
        Assert.Single(Directory.EnumerateFiles(_paths.PhotosDirectory));
        Assert.NotNull(_photos.Resolve(kept));
    }

    [Fact]
    public void Sweeping_with_no_folder_is_not_an_error()
        => Assert.Equal(0, _photos.CleanUpOrphans());

    // ------------------------------------------------------------------- reporting

    [Fact]
    public async Task Status_reports_what_is_on_disk()
    {
        _photos.SetEnabled(true);
        await Attach(AddEntry(), "one.png", "12345");

        var status = _photos.Status();

        Assert.True(status.Enabled);
        Assert.Equal(1, status.Count);
        Assert.Equal(5, status.Bytes);
    }

    // -------------------------------------------------------------------- fixtures

    private CollectionItem Single() => _collection.List().OrderBy(i => i.Id).First();

    private long AddEntry() => _collection.Add(new AddEntryRequest(CardId: "base1-4"));

    private Task<(bool Ok, string? Error)> Attach(long id, string fileName, string content = "image-bytes")
        => _photos.AttachAsync(id, new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName, default);

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
