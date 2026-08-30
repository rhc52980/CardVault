using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Cards worth being told have moved.
///
/// The threshold is a percentage AND an amount, and these pin down why: either alone
/// produces a list nobody reads. They also pin down that the comparison obeys the same
/// price rules as the grid — a movers list that disagreed with the totals above it
/// would be worse than none.
/// </summary>
public sealed class MoverTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly MoversService _movers;
    private readonly IConfiguration _config;

    public MoverTests()
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

        _config = config;
        var settings = new SettingsService(_db, config);
        _collection = new CollectionService(_db, settings, []);
        _movers = new MoversService(_db, settings, []);
        _movers.Save(new MoverSettings(Days: 30, MinPercent: 20, MinAmount: 5));
    }

    [Fact]
    public void The_defaults_are_small_enough_to_show_a_collection_of_commons()
    {
        // Nothing stored, so this reads what ships rather than what a previous run
        // chose. Twenty percent and five pounds -- the first draft -- returns nothing
        // for any collection made of commons, which is most of them, and a feature
        // that looks broken is worse than a noisy one.
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM settings WHERE key LIKE 'movers_%'";
            cmd.ExecuteNonQuery();
        }

        var shipped = new MoversService(_db, new SettingsService(_db, _config), []).Settings;
        Assert.True(shipped.MinAmount <= 1, $"amount was {shipped.MinAmount}");
        Assert.True(shipped.MinPercent is > 0 and <= 50, $"percent was {shipped.MinPercent}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------ the threshold

    [Fact]
    public void A_card_past_both_thresholds_is_flagged()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 150);

        var mover = Assert.Single(_movers.Find());
        Assert.Equal(100, mover.Was);
        Assert.Equal(150, mover.Now);
        Assert.Equal(50, mover.Change);
        Assert.Equal(50, mover.PercentChange);
    }

    /// <summary>
    /// The reason percentage alone is useless. A 50p card doubling is up 100% and is
    /// not news, and there are dozens of them in any collection.
    /// </summary>
    [Fact]
    public void A_big_percentage_on_a_trivial_amount_is_ignored()
    {
        Own("base1-58", "Pikachu");
        Price("base1-58", -20, 0.50);
        Price("base1-58", 0, 1.00);

        Assert.Empty(_movers.Find());
    }

    /// <summary>
    /// And the reason amount alone is useless: a £5 move on a £500 card is a 1% wobble,
    /// not a signal.
    /// </summary>
    [Fact]
    public void A_big_amount_on_a_trivial_percentage_is_ignored()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", -20, 500);
        Price("base1-4", 0, 506);

        Assert.Empty(_movers.Find());
    }

    /// <summary>
    /// The amount is judged on the holding, not the card, and this is the case that
    /// forced it: measured on a real bulk collection no single card moved by more than
    /// 90p in a fortnight, so a per-card floor showed nothing at all. Copies moving
    /// together is what carries weight there.
    /// </summary>
    [Fact]
    public void The_amount_threshold_counts_the_whole_holding()
    {
        _movers.Save(new MoverSettings(Days: 30, MinPercent: 25, MinAmount: 0.40));

        Own("base1-58", "Milcery", quantity: 8);
        Price("base1-58", -20, 0.15);
        Price("base1-58", 0, 0.06);

        // 9p a card is nothing; 72p off the holding is the thing worth knowing.
        var mover = Assert.Single(_movers.Find());
        Assert.Equal(-0.72, mover.LineChange);
    }

    [Fact]
    public void A_single_copy_moving_the_same_pennies_is_not_flagged()
    {
        _movers.Save(new MoverSettings(Days: 30, MinPercent: 25, MinAmount: 0.40));

        Own("base1-58", "Milcery", quantity: 1);
        Price("base1-58", -20, 0.15);
        Price("base1-58", 0, 0.06);

        Assert.Empty(_movers.Find());
    }

    [Fact]
    public void The_thresholds_can_be_changed()
    {
        Own("base1-58", "Pikachu");
        Price("base1-58", -20, 0.50);
        Price("base1-58", 0, 1.00);
        Assert.Empty(_movers.Find());

        _movers.Save(new MoverSettings(Days: 30, MinPercent: 20, MinAmount: 0));

        Assert.Single(_movers.Find());
    }

    // -------------------------------------------------------------------- the window

    [Fact]
    public void A_move_older_than_the_window_is_not_reported()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", -90, 100);
        Price("base1-4", -60, 200);

        Assert.Empty(_movers.Find());
    }

    /// <summary>One reading is not a movement, however old the card is.</summary>
    [Fact]
    public void A_card_with_a_single_reading_cannot_have_moved()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", 0, 100);

        Assert.Empty(_movers.Find());
    }

    [Fact]
    public void The_window_compares_the_ends_not_the_middle()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", -20, 100);
        Price("base1-4", -10, 500);  // a spike in between
        Price("base1-4", 0, 130);

        var mover = Assert.Single(_movers.Find());
        Assert.Equal(100, mover.Was);
        Assert.Equal(130, mover.Now);
    }

    // ---------------------------------------------------------------------- falls

    /// <summary>
    /// Found the same way and returned alongside: the arithmetic is identical, and
    /// "sell before it slides further" is the mirror of the decision a rise informs.
    /// </summary>
    [Fact]
    public void A_fall_is_reported_too()
    {
        Own("base1-4", "Charizard");
        Price("base1-4", -20, 200);
        Price("base1-4", 0, 100);

        var mover = Assert.Single(_movers.Find());
        Assert.Equal(-100, mover.Change);
        Assert.Equal(-50, mover.PercentChange);
    }

    // ------------------------------------------------------------------ your holding

    /// <summary>
    /// The figure that actually changes what the collection is worth. A £6 rise on a
    /// card you have eight of is £48.
    /// </summary>
    [Fact]
    public void The_change_to_your_holding_counts_the_copies()
    {
        Own("base1-4", "Charizard", quantity: 8);
        Price("base1-4", -20, 30);
        Price("base1-4", 0, 36);

        Assert.Equal(48, Assert.Single(_movers.Find()).LineChange);
    }

    [Fact]
    public void Movers_are_ordered_by_what_they_did_to_your_holding()
    {
        Own("base1-4", "Charizard", quantity: 1);
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 200);

        Own("base1-58", "Pikachu", quantity: 20);
        Price("base1-58", -20, 10);
        Price("base1-58", 0, 20);

        // The Pikachu moved less per card but 200 across the holding, against 100.
        Assert.Equal("Pikachu", _movers.Find()[0].Name);
    }

    // ------------------------------------------------------- the same rules as the grid

    /// <summary>
    /// A card you priced yourself hasn't moved because a market said so — it moved
    /// because you changed your mind, which is not news.
    /// </summary>
    [Fact]
    public void A_card_you_value_yourself_is_left_out()
    {
        var id = Own("base1-4", "Charizard");
        _collection.Update(id, new UpdateEntryRequest(ManualValue: 400));
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 200);

        Assert.Empty(_movers.Find());
    }

    /// <summary>Every figure held is for a raw card, so a slab's is not its own.</summary>
    [Fact]
    public void A_slab_is_left_out()
    {
        Own("base1-4", "Charizard", grade: "PSA 9");
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 200);

        Assert.Empty(_movers.Find());
    }

    /// <summary>Same reason: the prices describe the English printing.</summary>
    [Fact]
    public void A_non_english_copy_is_left_out()
    {
        Own("base1-4", "Charizard", language: "ja");
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 200);

        Assert.Empty(_movers.Find());
    }

    [Fact]
    public void A_card_you_do_not_own_is_not_reported()
    {
        SeedCard("base1-4", "Charizard");
        Price("base1-4", -20, 100);
        Price("base1-4", 0, 200);

        Assert.Empty(_movers.Find());
    }

    // -------------------------------------------------------------------- fixtures

    private long Own(string cardId, string name, int quantity = 1, string? grade = null, string language = "en")
    {
        SeedCard(cardId, name);
        return _collection.Add(new AddEntryRequest(
            CardId: cardId, Quantity: quantity, Grade: grade, Language: language));
    }

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

    /// <summary>A reading this many days ago. Negative is in the past.</summary>
    private void Price(string cardId, int daysAgo, double market)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, 'normal', 'tcgplayer', 'USD', $day, $market)
            ON CONFLICT(card_id, variant, source, captured_on) DO UPDATE SET market = excluded.market
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$day", DateTime.UtcNow.AddDays(daysAgo).ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$market", market);
        cmd.ExecuteNonQuery();
    }
}
