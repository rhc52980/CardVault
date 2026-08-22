using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Language is the one field on a collection row that changes what a card is worth,
/// because every price the app holds is for the English printing. These pin down both
/// halves: that whatever a CSV calls a language ends up as one stored code, and that a
/// non-English copy is left unpriced rather than quietly taking the English figure.
/// </summary>
public sealed class LanguageTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly SalesService _sales;

    public LanguageTests()
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
        _sales = new SalesService(_db, _collection);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ---------------------------------------------------------------- normalising

    [Theory]
    [InlineData("Japanese", "ja")]
    [InlineData("japanese", "ja")]
    [InlineData("JA", "ja")]
    [InlineData("jp", "ja")]
    [InlineData("JPN", "ja")]
    [InlineData("Japanese (JP)", "ja")]
    [InlineData("German", "de")]
    [InlineData("Deutsch", "de")]
    [InlineData("Traditional Chinese", "zh-tw")]
    [InlineData("zh-TW", "zh-tw")]
    [InlineData("pt-BR", "pt")]
    public void Whatever_a_csv_calls_a_language_becomes_one_code(string raw, string expected)
        => Assert.Equal(expected, Languages.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Klingon")]
    public void Nothing_recognisable_is_English(string? raw)
        => Assert.Equal("en", Languages.Normalize(raw));

    /// <summary>
    /// Unknown text is not kept verbatim. A free-text language column would defeat the
    /// point of having one, which is that the same printing always groups together.
    /// </summary>
    [Fact]
    public void An_unknown_language_is_not_stored_as_typed()
        => Assert.NotEqual("Klingon", Languages.Normalize("Klingon"));

    // ------------------------------------------------------------------ storage

    [Fact]
    public void A_card_added_without_a_language_is_English()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        Assert.Equal("en", Single().Language);
    }

    [Fact]
    public void A_language_is_normalised_on_the_way_in()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "Japanese"));

        Assert.Equal("ja", Single().Language);
    }

    [Fact]
    public void A_language_can_be_changed_afterwards()
    {
        SeedCard("base1-4", "Charizard");
        var id = _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        _collection.Update(id, new UpdateEntryRequest(Language: "jp"));

        Assert.Equal("ja", Single().Language);
    }

    /// <summary>
    /// The same card in two languages is two entries, not a duplicate to be merged:
    /// they are different objects that happen to share a catalogue row.
    /// </summary>
    [Fact]
    public void The_same_card_can_be_owned_in_two_languages()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "en"));
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "ja"));

        var items = _collection.List();
        Assert.Equal(2, items.Count);
        Assert.Equal(["en", "ja"], items.Select(i => i.Language).Order());
    }

    // ------------------------------------------------------------------ valuation

    [Fact]
    public void An_English_copy_takes_the_recorded_market_price()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 12.5);
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        var item = Single();
        Assert.True(item.Priced);
        Assert.Equal(12.5, item.MarketPrice);
        Assert.Equal(12.5, item.LineValue);
    }

    [Fact]
    public void A_Japanese_copy_does_not_take_the_English_price()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 12.5);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "ja"));

        var item = Single();
        Assert.False(item.Priced);
        Assert.Null(item.MarketPrice);
        Assert.Null(item.LineValue);
    }

    [Fact]
    public void A_Japanese_copy_is_left_out_of_the_collection_total()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 12.5);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "en"));
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "ja"));

        Assert.Equal(12.5, _collection.Stats().TotalMarketValue);
    }

    /// <summary>
    /// The escape hatch, and the same one slabs and sealed product already use: an
    /// unpriced copy is unpriced only until you say what it's worth.
    /// </summary>
    [Fact]
    public void Your_own_value_still_counts_on_a_Japanese_copy()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 12.5);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "ja", ManualValue: 40, Quantity: 2));

        var item = Single();
        Assert.False(item.Priced);
        Assert.Null(item.MarketPrice);
        Assert.Equal(80, item.LineValue);
        Assert.Equal(80, _collection.Stats().TotalMarketValue);
    }

    /// <summary>
    /// The history is built from the same English snapshots, so it has to exclude the
    /// same copies. Counting a Japanese card here would put value in the chart that
    /// the total below it never shows.
    /// </summary>
    [Fact]
    public void Value_history_counts_only_the_copies_the_total_counts()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 10);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "en", Quantity: 2));
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "ja", Quantity: 5));

        var history = _collection.ValueHistory();
        Assert.Equal(20, Assert.Single(history).Value);
    }

    /// <summary>
    /// The exception, and the reason it isn't arbitrary: a hand-entered item is priced
    /// by searching eBay for the name you typed, so "Japanese Base Set Charizard" is
    /// already a Japanese price. Voiding it would throw away the only figure there is.
    /// </summary>
    [Fact]
    public void A_hand_entered_item_keeps_its_price_in_any_language()
    {
        SeedCard("custom-1", "Japanese Base Set Charizard", isCustom: true);
        SeedPrice("custom-1", 500, variant: "custom");
        _collection.Add(new AddEntryRequest(CardId: "custom-1", Variant: "custom", Language: "ja"));

        var item = Single();
        Assert.True(item.Priced);
        Assert.Equal(500, item.MarketPrice);
        Assert.Equal(500, _collection.Stats().TotalMarketValue);
        Assert.Equal(500, Assert.Single(_collection.ValueHistory()).Value);
    }

    // --------------------------------------------------------------------- sales

    [Fact]
    public void Selling_a_card_records_what_language_it_was()
    {
        SeedCard("base1-4", "Charizard");
        var id = _collection.Add(new AddEntryRequest(CardId: "base1-4", Language: "Japanese"));

        var (ok, error, _) = _sales.Sell(id, new SellRequest(Quantity: 1, SalePrice: 100));

        Assert.True(ok, error);
        Assert.Equal("ja", Assert.Single(_sales.List()).Language);
    }

    // ------------------------------------------------------------------- fixtures

    private CollectionItem Single() => Assert.Single(_collection.List());

    private void SeedCard(string cardId, string name, bool isCustom = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at, is_custom)
            VALUES ($id, $name, '1', $payload, $now, $isCustom)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"{{name}}"}""");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$isCustom", isCustom ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A price the way a snapshot records one. The payload seeded above carries none of
    /// its own, so this is the only figure in play and the tests can be exact about it.
    /// </summary>
    private void SeedPrice(string cardId, double market, string variant = "normal")
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, $variant, 'tcgplayer', 'USD', '2026-08-01', $market)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$variant", variant);
        cmd.Parameters.AddWithValue("$market", market);
        cmd.ExecuteNonQuery();
    }
}
