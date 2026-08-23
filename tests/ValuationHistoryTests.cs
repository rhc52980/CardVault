using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// Sealed product and slabs usually have exactly one price: the one you typed.
/// Nothing external prices them dependably, so unless your own figure is recorded
/// their chart stays empty forever. These pin down that revaluing leaves a trail,
/// and — more importantly — that your own number is never handed back to you as
/// though a market had said it.
/// </summary>
public sealed class ValuationHistoryTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;

    public ValuationHistoryTests()
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
        SeedCard("custom-1");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void Setting_a_value_when_adding_records_it()
    {
        _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));

        Assert.Equal(900, LatestValuation());
    }

    [Fact]
    public void Revaluing_updates_the_recorded_figure()
    {
        var id = _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));

        _collection.Update(id, new UpdateEntryRequest(ManualValue: 1250));

        Assert.Equal(1250, LatestValuation());
    }

    /// <summary>
    /// Keyed by day like every other source. Changing your mind three times in an
    /// afternoon is one opinion, not three data points.
    /// </summary>
    [Fact]
    public void Several_changes_in_one_day_leave_one_point()
    {
        var id = _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));
        _collection.Update(id, new UpdateEntryRequest(ManualValue: 1100));
        _collection.Update(id, new UpdateEntryRequest(ManualValue: 1250));

        Assert.Equal(1, ValuationRows());
        Assert.Equal(1250, LatestValuation());
    }

    [Fact]
    public void An_edit_that_never_mentions_the_value_records_nothing()
    {
        var id = _collection.Add(new AddEntryRequest(CardId: "custom-1"));

        _collection.Update(id, new UpdateEntryRequest(Location: "Shelf B"));

        Assert.Equal(0, ValuationRows());
    }

    [Fact]
    public void Clearing_a_value_is_not_a_valuation()
    {
        var id = _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));

        _collection.Update(id, new UpdateEntryRequest(ClearManualValue: true));

        // The earlier figure stands as history; clearing simply doesn't add another.
        Assert.Equal(1, ValuationRows());
        Assert.Equal(900, LatestValuation());
    }

    /// <summary>
    /// The one that matters. Your own figure shares a table with fetched prices so the
    /// chart can draw it, and it must never come back out as the market price — that
    /// would quote your guess to you as though somebody else had made it.
    /// </summary>
    [Fact]
    public void Your_own_valuation_is_never_reported_as_the_market_price()
    {
        _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));

        var item = Assert.Single(_collection.List());
        Assert.Equal(900, item.ManualValue);
        Assert.Null(item.MarketPrice);
    }

    /// <summary>
    /// The grid values a card at your figure when you've set one, so the history has
    /// to agree — a chart that disagreed with the total above it would be worse than
    /// no chart.
    /// </summary>
    [Fact]
    public void Value_history_follows_your_figure_over_the_market()
    {
        SeedPrice("custom-1", 400);
        _collection.Add(new AddEntryRequest(CardId: "custom-1", ManualValue: 900));

        Assert.Equal(900, Assert.Single(_collection.ValueHistory()).Value);
    }

    // ------------------------------------------------------------------- fixtures

    private double? LatestValuation()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT market FROM price_history WHERE source = $s ORDER BY captured_on DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$s", Valuations.Source);
        return cmd.ExecuteScalar() as double?;
    }

    private long ValuationRows()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM price_history WHERE source = $s";
        cmd.Parameters.AddWithValue("$s", Valuations.Source);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void SeedCard(string cardId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at, is_custom)
            VALUES ($id, 'Booster Box', '1', '{"id":"x","name":"Booster Box"}', $now, 1)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void SeedPrice(string cardId, double market)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, 'normal', 'ebay-asking', 'USD', $today, $market)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$today", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$market", market);
        cmd.ExecuteNonQuery();
    }
}
