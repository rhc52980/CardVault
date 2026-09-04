using CardVault.Data;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Which cards a gap-filling refresh would touch.
///
/// The full sweep is one API call per card with pacing between them, so a few
/// thousand cards take minutes. After an import the cards you are actually waiting
/// on are the handful that arrived without a price — cards added from the offline
/// catalogue come in unpriced by design, because the add never touches the network
/// and so cannot fail, with a background fetch catching up afterwards. When that
/// fetch doesn't land, this is the set worth asking about again.
///
/// The two judgement calls are what these pin down. A row whose market is null is
/// how a source records "asked, got nothing", and a card in that state still has a
/// gap. And a price from a source you aren't looking at is not a price: the vault
/// shows one source's figures, so a card priced only on eBay reads as unpriced in a
/// vault set to TCGplayer.
/// </summary>
public sealed class UnpricedTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly PriceSnapshotService _snapshots;

    public UnpricedTests()
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
        settings.PreferredPriceSource = "tcgplayer";

        // Nothing here goes near the network: the queries under test only read what
        // is already on disk.
        _snapshots = new PriceSnapshotService(
            _db,
            new PokemonTcgClient(new HttpClient(), settings, NullLogger<PokemonTcgClient>.Instance),
            new CardCache(_db),
            [],
            settings,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<PriceSnapshotService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void A_card_with_no_price_at_all_is_a_gap()
    {
        Own("base1-4");

        Assert.Equal(1, _snapshots.UnpricedCount());
    }

    [Fact]
    public void A_priced_card_is_not()
    {
        Own("base1-4");
        Price("base1-4", "tcgplayer", 12.50);

        Assert.Equal(0, _snapshots.UnpricedCount());
    }

    /// <summary>
    /// "Asked, got nothing" is how a source records a card it could not put a number
    /// on, and the gap it leaves is exactly the one this fills. Counting the row as an
    /// answer would mean a card that failed once is never asked about again.
    /// </summary>
    [Fact]
    public void A_row_with_no_figure_in_it_is_still_a_gap()
    {
        Own("base1-4");
        Price("base1-4", "tcgplayer", null);

        Assert.Equal(1, _snapshots.UnpricedCount());
    }

    /// <summary>
    /// The vault shows one source's figures at a time, so a price from another one
    /// leaves the card blank on screen — which is the thing being asked about.
    /// </summary>
    [Fact]
    public void A_price_from_a_source_you_are_not_looking_at_is_not_a_price()
    {
        Own("base1-4");
        Price("base1-4", "ebay", 40.00);

        Assert.Equal(1, _snapshots.UnpricedCount());
    }

    /// <summary>A card on the wants list is priced too, so a gap there counts.</summary>
    [Fact]
    public void A_wanted_card_counts()
    {
        Want("base1-4");

        Assert.Equal(1, _snapshots.UnpricedCount());
    }

    /// <summary>
    /// Two copies of a card are one gap. The refresh is per card, and counting the
    /// entries would promise a longer run than it performs.
    /// </summary>
    [Fact]
    public void Two_copies_of_one_card_are_one_gap()
    {
        Own("base1-4");
        Own("base1-4");

        Assert.Equal(1, _snapshots.UnpricedCount());
    }

    [Fact]
    public void A_card_you_neither_own_nor_want_is_not_counted()
    {
        SeedCard("base1-4");

        Assert.Equal(0, _snapshots.UnpricedCount());
    }

    // -------------------------------------------------------------------- fixtures

    private void Own(string cardId)
    {
        SeedCard(cardId);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collection (card_id, quantity, variant, condition, added_at, language)
            VALUES ($id, 1, 'normal', 'NM', $now, 'en')
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void Want(string cardId)
    {
        SeedCard(cardId);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO wants (card_id, added_at) VALUES ($id, $now)";
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void SeedCard(string cardId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO cards (id, name, set_name, number, payload, cached_at)
            VALUES ($id, 'Charizard', 'Base', '4', '{}', $now)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void Price(string cardId, string source, double? market)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, 'normal', $source, 'USD', $day, $market)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$day", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$market", (object?)market ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
