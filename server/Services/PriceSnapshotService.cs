using System.Text.Json;
using PokemonVault.Data;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>
/// Refreshes prices for every card you own and records one snapshot row per
/// card+variant+day. The API only ever reports today's price, so this table is the
/// only way to answer "what was my collection worth last month?".
/// </summary>
public sealed class PriceSnapshotService(
    Db db,
    PokemonTcgClient api,
    CardCache cache,
    ILogger<PriceSnapshotService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the web host settle before hitting the network.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var captured = await CaptureAsync(ct);
                if (captured > 0) log.LogInformation("Captured prices for {Count} cards", captured);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Price snapshot failed; will retry on the next cycle");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Re-fetches every owned card and writes today's prices. Safe to call repeatedly —
    /// the snapshot table is keyed by day, so a second run just overwrites today.
    /// </summary>
    public async Task<int> CaptureAsync(CancellationToken ct)
    {
        var cardIds = OwnedCardIds();
        if (cardIds.Count == 0) return 0;

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var captured = 0;

        // One request per card. Batching with "id:a OR id:b" looks tempting but the
        // API 500s on those queries, and the per-card endpoint is reliable.
        foreach (var id in cardIds)
        {
            ct.ThrowIfCancellationRequested();

            JsonElement? card;
            try
            {
                card = await api.GetCardAsync(id, ct);
            }
            catch (Exception e)
            {
                // One bad card shouldn't abandon the rest of the collection.
                log.LogWarning(e, "Could not refresh prices for {CardId}", id);
                continue;
            }

            if (card is null) continue;
            cache.Upsert(card.Value);

            foreach (var variant in Pricing.AvailableVariants(card.Value))
                RecordSnapshot(id, variant, today, Pricing.ForVariant(card.Value, variant));

            captured++;

            // Gentle pacing so a large collection doesn't trip the rate limit.
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return captured;
    }

    /// <summary>
    /// Writes today's prices for a single card straight from what's already cached.
    ///
    /// Called when a card is added so its chart has a point immediately. Without
    /// this the first data point wouldn't appear until the next daily cycle, and a
    /// card added this morning would show an empty chart all day.
    /// </summary>
    public void RecordCurrentPrices(string cardId)
    {
        var payload = cache.GetPayload(cardId);
        if (payload is null) return;

        using var doc = JsonDocument.Parse(payload);
        var card = doc.RootElement;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        foreach (var variant in Pricing.AvailableVariants(card))
            RecordSnapshot(cardId, variant, today, Pricing.ForVariant(card, variant));
    }

    /// <summary>Price points for one card, newest last, grouped by printing.</summary>
    public Dictionary<string, List<PricePoint>> HistoryFor(string cardId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT variant, captured_on, market, low, high
            FROM price_history
            WHERE card_id = $cardId AND market IS NOT NULL
            ORDER BY captured_on
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);

        var series = new Dictionary<string, List<PricePoint>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var variant = r.GetString(0);
            if (!series.TryGetValue(variant, out var points))
                series[variant] = points = [];

            points.Add(new PricePoint(
                Date: r.GetString(1),
                Market: r.GetDouble(2),
                Low: r.IsDBNull(3) ? null : r.GetDouble(3),
                High: r.IsDBNull(4) ? null : r.GetDouble(4)));
        }

        return series;
    }

    /// <summary>
    /// Cards worth refreshing: everything you own, plus everything on the want list.
    /// A want with a target price is only useful if its market price is current.
    /// </summary>
    private List<string> OwnedCardIds()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // Custom items have no catalogue entry to refresh — their value is yours to set.
        cmd.CommandText = """
            SELECT DISTINCT card_id FROM (
                SELECT c.card_id AS card_id
                FROM collection c
                JOIN cards k ON k.id = c.card_id
                WHERE COALESCE(k.is_custom, 0) = 0
                UNION
                SELECT w.card_id
                FROM wants w
                JOIN cards k2 ON k2.id = w.card_id
                WHERE COALESCE(k2.is_custom, 0) = 0
            )
            """;
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private void RecordSnapshot(string cardId, string variant, string day, PriceSet p)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, captured_on, market, low, mid, high)
            VALUES ($cardId, $variant, $day, $market, $low, $mid, $high)
            ON CONFLICT(card_id, variant, captured_on) DO UPDATE SET
                market=excluded.market, low=excluded.low, mid=excluded.mid, high=excluded.high;
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);
        cmd.Parameters.AddWithValue("$variant", variant);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$market", (object?)p.Market ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$low", (object?)p.Low ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)p.Mid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$high", (object?)p.High ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
