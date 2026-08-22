using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// A slab is worth a multiple of the raw card, sometimes a fraction of it, and never
/// the same. Valuing one at the raw price was the last place the app showed a figure
/// it had no basis for. These pin down that it no longer does, and that reading a
/// company and a number out of the grade text can't be fooled by a cert number.
/// </summary>
public sealed class GradeTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;

    public GradeTests()
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
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // -------------------------------------------------------------------- reading

    [Theory]
    [InlineData("PSA 9", "PSA", 9)]
    [InlineData("psa10", "PSA", 10)]
    [InlineData("BGS 9.5", "BGS", 9.5)]
    [InlineData("CGC 10 Pristine", "CGC", 10)]
    [InlineData("SGC 8.5", "SGC", 8.5)]
    [InlineData("Graded PSA 7 by a shop", "PSA", 7)]
    public void A_company_and_a_number_are_read_out_of_the_text(
        string raw, string company, double value)
    {
        Assert.Equal(company, Grades.Company(raw));
        Assert.Equal(value, Grades.Value(raw));
    }

    [Fact]
    public void A_bare_number_is_a_grade_with_no_company()
    {
        Assert.Null(Grades.Company("9"));
        Assert.Equal(9, Grades.Value("9"));
    }

    /// <summary>
    /// The importer maps a Cert column onto Grade, so serials land in this field in
    /// practice. A leading "09" is a perfectly plausible grade of 9, which is exactly
    /// why a digit on either side has to disqualify the match outright.
    /// </summary>
    [Theory]
    [InlineData("12345678")]
    [InlineData("09876543")]
    [InlineData("PSA 12345678")]
    public void A_cert_number_is_not_read_as_a_grade(string raw)
        => Assert.Null(Grades.Value(raw));

    [Fact]
    public void A_cert_number_still_names_its_company()
        => Assert.Equal("PSA", Grades.Company("PSA 12345678"));

    /// <summary>
    /// "Authentic" is a real slab with no number on it. It stays graded; there is
    /// simply nothing to sort it by.
    /// </summary>
    [Fact]
    public void An_unnumbered_designation_is_still_graded()
    {
        Assert.True(Grades.IsGraded("PSA Authentic"));
        Assert.Null(Grades.Value("PSA Authentic"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_the_box_means_not_graded(string? raw)
        => Assert.False(Grades.IsGraded(raw));

    /// <summary>
    /// Whether a card is graded is not the parser's call. Text it can't read is still
    /// you telling it you own a slab.
    /// </summary>
    [Fact]
    public void Text_the_parser_cannot_read_is_still_a_grade()
    {
        Assert.True(Grades.IsGraded("slabbed, label faded"));
        Assert.Null(Grades.Company("slabbed, label faded"));
        Assert.Null(Grades.Value("slabbed, label faded"));
    }

    [Theory]
    [InlineData("psa10", "PSA 10")]
    [InlineData("BGS 9.5", "BGS 9.5")]
    [InlineData("slabbed", "slabbed")]
    public void A_label_tidies_what_it_can_and_keeps_the_rest(string raw, string expected)
        => Assert.Equal(expected, Grades.Label(raw));

    // ------------------------------------------------------------------ valuation

    [Fact]
    public void A_raw_card_takes_the_market_price()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 855.52);
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));

        var item = Single();
        Assert.True(item.Priced);
        Assert.Equal(855.52, item.MarketPrice);
        Assert.Null(item.ReferencePrice);
        Assert.Null(item.UnpricedReason);
    }

    [Fact]
    public void A_slab_does_not_take_the_raw_price()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 855.52);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 9"));

        var item = Single();
        Assert.False(item.Priced);
        Assert.Null(item.MarketPrice);
        Assert.Null(item.LineValue);
        Assert.Equal("graded", item.UnpricedReason);
    }

    /// <summary>
    /// Not counted is not the same as thrown away. The raw figure is what you'd judge
    /// a slab against, so losing it would mean looking the card up somewhere else.
    /// </summary>
    [Fact]
    public void The_raw_price_is_kept_as_a_reference()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 855.52);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 9"));

        Assert.Equal(855.52, Single().ReferencePrice);
    }

    [Fact]
    public void A_slab_is_left_out_of_the_collection_total()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 855.52);
        _collection.Add(new AddEntryRequest(CardId: "base1-4"));
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 9"));

        Assert.Equal(855.52, _collection.Stats().TotalMarketValue);
    }

    [Fact]
    public void Your_own_value_still_counts_on_a_slab()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 855.52);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 9", ManualValue: 4000));

        var item = Single();
        Assert.False(item.Priced);
        Assert.Equal(4000, item.LineValue);
        Assert.Equal(4000, _collection.Stats().TotalMarketValue);
        Assert.Equal(855.52, item.ReferencePrice);
    }

    [Fact]
    public void Value_history_counts_only_the_copies_the_total_counts()
    {
        SeedCard("base1-4", "Charizard");
        SeedPrice("base1-4", 100);
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Quantity: 2));
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 10", Quantity: 5));

        Assert.Equal(200, Assert.Single(_collection.ValueHistory()).Value);
    }

    /// <summary>
    /// A slab is a slab before it is a Japanese card: the grade is the bigger reason
    /// the raw figure is wrong, so that's the one worth explaining.
    /// </summary>
    [Fact]
    public void A_graded_import_in_another_language_reports_the_grade_as_the_reason()
    {
        SeedCard("base1-4", "Charizard");
        _collection.Add(new AddEntryRequest(CardId: "base1-4", Grade: "PSA 10", Language: "ja"));

        Assert.Equal("graded", Single().UnpricedReason);
    }

    /// <summary>
    /// Hand-entered slabs are the exception, same as for language: an eBay search on
    /// "Base Set Charizard PSA 10" is already a graded price, so it stands.
    /// </summary>
    [Fact]
    public void A_hand_entered_slab_keeps_its_price()
    {
        SeedCard("custom-1", "Base Set Charizard PSA 10", isCustom: true);
        SeedPrice("custom-1", 12000, variant: "custom");
        _collection.Add(new AddEntryRequest(CardId: "custom-1", Variant: "custom", Grade: "PSA 10"));

        var item = Single();
        Assert.True(item.Priced);
        Assert.Equal(12000, item.MarketPrice);
        Assert.Equal(12000, _collection.Stats().TotalMarketValue);
    }

    // -------------------------------------------------------------------- fixtures

    private CollectionItem Single() => Assert.Single(_collection.List());

    private void SeedCard(string cardId, string name, bool isCustom = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at, is_custom)
            VALUES ($id, $name, '1', $payload, $now, $custom)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", $$"""{"id":"{{cardId}}","name":"{{name}}"}""");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$custom", isCustom ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

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
