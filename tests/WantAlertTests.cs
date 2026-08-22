using System.Text.Json;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// The want list has always compared the market against your target. What it could
/// not say is when that happened, which is the difference between news and a price
/// you have already decided not to pay. These pin down the stamping, and that a want
/// quotes the same figure the collection grid would for the same card.
/// </summary>
public sealed class WantAlertTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly WantsService _wants;

    public WantAlertTests()
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
        var collection = new CollectionService(_db, settings, []);
        _wants = new WantsService(_db, collection, settings, []);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------ crossing

    [Fact]
    public void A_want_above_its_target_is_not_stamped()
    {
        SeedCard("base1-4", market: 100);
        _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));

        var want = Single();
        Assert.False(want.AtOrBelowTarget);
        Assert.Null(want.MetSince);
    }

    [Fact]
    public void A_want_at_its_target_is_stamped()
    {
        SeedCard("base1-4", market: 50);
        _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));

        var want = Single();
        Assert.True(want.AtOrBelowTarget);
        Assert.NotNull(want.MetSince);
    }

    [Fact]
    public void Crossing_is_reported_once_and_not_again()
    {
        SeedCard("base1-4", market: 100);
        _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));

        SetMarket("base1-4", 40);
        Assert.Single(_wants.RefreshAlerts());

        // Still under target, but it crossed on the previous pass, not this one.
        Assert.Empty(_wants.RefreshAlerts());
    }

    /// <summary>
    /// The stamp describes the current run, not the best the price ever did. Without
    /// clearing it, a card that dipped once would claim to have been at your price
    /// ever since, through however many months of being nowhere near it.
    /// </summary>
    [Fact]
    public void Going_back_above_the_target_clears_the_stamp()
    {
        SeedCard("base1-4", market: 40);
        _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));
        Assert.NotNull(Single().MetSince);

        SetMarket("base1-4", 90);

        var want = Single();
        Assert.False(want.AtOrBelowTarget);
        Assert.Null(want.MetSince);
    }

    [Fact]
    public void A_stamp_survives_while_the_price_stays_down()
    {
        SeedCard("base1-4", market: 40);
        _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));
        var first = Single().MetSince;

        SetMarket("base1-4", 45);

        Assert.Equal(first, Single().MetSince);
    }

    /// <summary>
    /// Dropping the target is not the same as the price rising, but it does mean
    /// there is no longer a target being met, so the stamp has to go with it.
    /// </summary>
    [Fact]
    public void Clearing_the_target_clears_the_stamp()
    {
        SeedCard("base1-4", market: 40);
        var id = _wants.Add(new AddWantRequest("base1-4", TargetPrice: 50));
        Assert.NotNull(Single().MetSince);

        _wants.Update(id, new UpdateWantRequest(ClearTarget: true));

        var want = Single();
        Assert.False(want.AtOrBelowTarget);
        Assert.Null(want.MetSince);
    }

    [Fact]
    public void A_want_with_no_target_is_never_stamped()
    {
        SeedCard("base1-4", market: 1);
        _wants.Add(new AddWantRequest("base1-4"));

        Assert.Null(Single().MetSince);
    }

    // ------------------------------------------------------------------- pricing

    /// <summary>
    /// A card the catalogue can't price — sealed product, anything only eBay has seen
    /// — used to sit on the list with no figure and could never trip its target. It
    /// now falls back to the last recorded price, which is the rule the grid uses.
    /// </summary>
    [Fact]
    public void A_want_falls_back_to_the_last_recorded_price()
    {
        SeedCard("custom-1", market: null);
        SeedPrice("custom-1", 30);
        _wants.Add(new AddWantRequest("custom-1", TargetPrice: 50));

        var want = Single();
        Assert.Equal(30, want.MarketPrice);
        Assert.True(want.AtOrBelowTarget);
    }

    // ------------------------------------------------------------------ fixtures

    private WantItem Single() => Assert.Single(_wants.List());

    /// <summary>A card whose catalogue payload carries a TCGplayer market price.</summary>
    private void SeedCard(string cardId, double? market)
    {
        // Built with the serialiser rather than by interpolating braces into a raw
        // string: the nesting is four deep and the escaping stops being readable.
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = cardId,
            ["name"] = "Charizard",
            ["tcgplayer"] = new Dictionary<string, object>
            {
                ["prices"] = market is { } m
                    ? new Dictionary<string, object> { ["normal"] = new Dictionary<string, object> { ["market"] = m } }
                    : new Dictionary<string, object>(),
            },
        });

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, payload, cached_at)
            VALUES ($id, 'Charizard', '4', $payload, $now)
            ON CONFLICT(id) DO UPDATE SET payload = excluded.payload
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void SetMarket(string cardId, double market) => SeedCard(cardId, market);

    private void SeedPrice(string cardId, double market)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market)
            VALUES ($id, 'normal', 'tcgplayer', 'USD', '2026-08-01', $market)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$market", market);
        cmd.ExecuteNonQuery();
    }
}
