using System.Text.Json;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services.PriceSources;

namespace CardVault.Services;

/// <summary>
/// Refreshes prices for every card you own or want, recording one row per
/// card + printing + source + day. The API only ever reports today's price, so
/// this table is the only way to answer "what was my collection worth last month?".
///
/// Every registered source is asked about every card, so adding a source is a
/// registration rather than a change here.
/// </summary>
public sealed class PriceSnapshotService(
    Db db,
    PokemonTcgClient api,
    CardCache cache,
    IEnumerable<IPriceSource> sources,
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

            await RecordFromAllSourcesAsync(id, card.Value, today, ct);
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
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Sources that read the cached payload complete synchronously; this only
        // blocks if a future source goes to the network, which the caller (adding
        // a card) can afford.
        RecordFromAllSourcesAsync(cardId, doc.RootElement, today, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asks every registered source about this card and records whatever comes
    /// back. A source that fails is logged and skipped — one market being down
    /// must not cost you the others.
    /// </summary>
    private async Task RecordFromAllSourcesAsync(string cardId, JsonElement card, string day, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            if (!source.CanPrice(cardId, card)) continue;

            try
            {
                foreach (var price in await source.GetPricesAsync(cardId, card, ct))
                {
                    RecordSnapshot(cardId, price.Variant, source.Id, source.Currency, day,
                        new PriceSet(price.Market, price.Low, price.Mid, price.High));
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Price source {Source} failed for {CardId}", source.Id, cardId);
            }
        }
    }

    /// <summary>
    /// Price points for one card, newest last, as one series per printing and
    /// source. Currency rides along so the chart can label a euro line as euros
    /// rather than implying it's comparable to a dollar one.
    /// </summary>
    public List<PriceSeries> HistoryFor(string cardId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT variant, source, currency, captured_on, market, low, high
            FROM price_history
            WHERE card_id = $cardId AND market IS NOT NULL
            ORDER BY source, variant, captured_on
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);

        var grouped = new Dictionary<(string Variant, string Source), (string Currency, List<PricePoint> Points)>();

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = (Variant: r.GetString(0), Source: r.GetString(1));
            if (!grouped.TryGetValue(key, out var entry))
                grouped[key] = entry = (r.GetString(2), []);

            entry.Points.Add(new PricePoint(
                Date: r.GetString(3),
                Market: r.GetDouble(4),
                Low: r.IsDBNull(5) ? null : r.GetDouble(5),
                High: r.IsDBNull(6) ? null : r.GetDouble(6)));
        }

        return grouped
            .Select(kv => new PriceSeries(
                Variant: kv.Key.Variant,
                Source: kv.Key.Source,
                SourceName: sources.FirstOrDefault(s => s.Id == kv.Key.Source)?.DisplayName ?? kv.Key.Source,
                Currency: kv.Value.Currency,
                Points: kv.Value.Points))
            .OrderBy(s => s.Source)
            .ThenBy(s => s.Variant)
            .ToList();
    }

    /// <summary>Sources currently registered, for the settings screen.</summary>
    public IReadOnlyList<PriceSourceInfo> Sources() =>
        sources.Select(s => new PriceSourceInfo(s.Id, s.DisplayName, s.Currency)).ToList();

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

    private void RecordSnapshot(string cardId, string variant, string source, string currency, string day, PriceSet p)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history (card_id, variant, source, currency, captured_on, market, low, mid, high)
            VALUES ($cardId, $variant, $source, $currency, $day, $market, $low, $mid, $high)
            ON CONFLICT(card_id, variant, source, captured_on) DO UPDATE SET
                currency=excluded.currency, market=excluded.market,
                low=excluded.low, mid=excluded.mid, high=excluded.high;
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);
        cmd.Parameters.AddWithValue("$variant", variant);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$currency", currency);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$market", (object?)p.Market ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$low", (object?)p.Low ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)p.Mid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$high", (object?)p.High ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
