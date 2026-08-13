using System.Text.Json;
using PokemonVault.Data;

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

    private List<string> OwnedCardIds()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT card_id FROM collection";
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
