using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// A number and its denominator identify a card; a name is a hint about it.
///
/// They fail in different ways, and that is the whole point. "106/189" is read off a
/// fixed spot on the card and is either right or missing. A name is what OCR and
/// hurried typing get wrong — and because the lookup ANDs its clauses, a name that is
/// slightly off used to hide the very card the number had already found. These pin
/// down that it no longer can, while a correct name still decides between printings.
/// </summary>
public sealed class NumberFirstTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CatalogueService _catalogue;

    public NumberFirstTests()
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
        // No network is touched by the lookups under test, so the factory is never used.
        _catalogue = new CatalogueService(
            _db, paths, settings, new UnusedHttpClientFactory(), NullLogger<CatalogueService>.Instance);

        Seed("swsh3-106", "Grimmsnarl", "swsh3", "Darkness Ablaze", "106", 189);
        Seed("swsh10-106", "Lucario", "swsh10", "Astral Radiance", "106", 189);
        Seed("swsh3-020", "Charmander", "swsh3", "Darkness Ablaze", "20", 189);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    /// <summary>
    /// The case this exists for. OCR reads "Grimmsnarl" as "Grimmsnari"; the number is
    /// perfect. Before, the name clause hid the card and the row fell through to the
    /// network for an answer that was already sitting on disk.
    /// </summary>
    [Fact]
    public void A_misread_name_no_longer_hides_the_card_the_number_found()
    {
        var found = _catalogue.FindCards(null, "106", "Grimmsnari", 189);

        Assert.NotEmpty(found);
        Assert.Contains(found, c => c.Id == "swsh3-106");
    }

    [Fact]
    public void A_number_and_denominator_alone_are_enough()
    {
        var found = _catalogue.FindCards(null, "106", null, 189);

        Assert.Equal(2, found.Count);
    }

    /// <summary>
    /// The name still earns its place when it is right: it is what separates two cards
    /// that share a number and a set size.
    /// </summary>
    [Fact]
    public void A_correct_name_still_narrows_to_one()
    {
        var found = _catalogue.FindCards(null, "106", "Lucario", 189);

        Assert.Equal("swsh10-106", Assert.Single(found).Id);
    }

    [Fact]
    public void A_number_that_matches_nothing_still_finds_nothing()
        => Assert.Empty(_catalogue.FindCards(null, "999", "Grimmsnarl", 189));

    /// <summary>
    /// Loosening must not run away. Dropping the name is allowed; answering a question
    /// about 106 with a card numbered 20 is not.
    /// </summary>
    [Fact]
    public void Loosening_never_abandons_the_number()
    {
        var found = _catalogue.FindCards(null, "106", "Charmander", 189);

        Assert.All(found, c => Assert.Equal("106", c.Number));
    }

    /// <summary>
    /// A wrong denominator is worth loosening too — the second fallback — but only
    /// after the name has been tried without it.
    /// </summary>
    [Fact]
    public void A_wrong_denominator_falls_back_to_the_number()
    {
        var found = _catalogue.FindCards(null, "106", null, 999);

        Assert.Equal(2, found.Count);
        Assert.All(found, c => Assert.Equal("106", c.Number));
    }

    /// <summary>These tests only read the local catalogue; nothing here goes out.</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    // ------------------------------------- the number is the one thing never given up

    /// <summary>
    /// The bug this pins down, reported from a real import. A CSV row read
    /// "Purrloin, Darkness Ablaze, 106/189". The precise query returned 500 — which
    /// pokemontcg.io does constantly — the search fell through to the card's name
    /// alone, and a Purrloin from another set matched with nothing left to contradict
    /// it. The row imported as Matched, confidently, at 096/159.
    ///
    /// Every other field degrades politely here: a name that matches nothing is
    /// ignored rather than allowed to empty the list, which is right for fields that
    /// get misread. Applying that same courtesy to the number is how a collection
    /// quietly fills with the wrong printings.
    /// </summary>
    [Theory]
    [InlineData("106", "096")]
    [InlineData("4", "104")]
    public void A_card_at_a_different_number_is_never_the_answer(string asked, string other)
    {
        var wrong = Candidate("x-1", "Purrloin", other);

        Assert.Empty(ImportService.NarrowByNumber([wrong], asked));
    }

    [Fact]
    public void The_right_number_survives()
    {
        var right = Candidate("x-1", "Purrloin", "106");

        Assert.Single(ImportService.NarrowByNumber([right], "106"));
    }

    /// <summary>Leading zeros are formatting, not identity.</summary>
    [Theory]
    [InlineData("106", "106")]
    [InlineData("006", "6")]
    [InlineData("6", "006")]
    [InlineData("TG12", "tg12")]
    public void The_same_number_written_differently_still_matches(string asked, string stored)
        => Assert.Single(ImportService.NarrowByNumber([Candidate("x-1", "Purrloin", stored)], asked));

    /// <summary>
    /// With no number to check against there is nothing to enforce, and a row that
    /// only gave a name must still be able to match on it.
    /// </summary>
    [Fact]
    public void A_row_with_no_number_is_left_alone()
        => Assert.Single(ImportService.NarrowByNumber([Candidate("x-1", "Purrloin", "96")], null));

    private static CardCandidate Candidate(string id, string name, string number)
        => new(id, name, "Some Set", number, Rarity: null, ImageSmall: null,
               MarketPrice: null, Variants: [], PrintedTotal: null);

    private void Seed(string id, string name, string setId, string setName, string number, int total)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO catalogue
                (id, name, set_id, set_name, set_series, number, printed_total,
                 rarity, supertype, subtypes, types, artist, release_date, image_url, has_image)
            VALUES ($id, $name, $setId, $setName, 'Sword & Shield', $number, $total,
                    'Common', 'Pokémon', '[]', '[]', 'x', '2020-01-01', NULL, 0)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$setId", setId);
        cmd.Parameters.AddWithValue("$setName", setName);
        cmd.Parameters.AddWithValue("$number", number);
        cmd.Parameters.AddWithValue("$total", total);
        cmd.ExecuteNonQuery();
    }
}
