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
    // Resolved lazily rather than injected: wants are refreshed as a consequence of a
    // capture, not something a capture needs in order to run, and taking the service
    // directly would tie this one's construction to a chain it has no business in.
    IServiceProvider services,
    ILogger<PriceSnapshotService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Gap between calls when catching up a batch, matching the import's own.</summary>
    private static readonly TimeSpan EnrichPacing = TimeSpan.FromMilliseconds(150);

    private readonly object _refreshLock = new();
    private PriceRefreshProgress _refresh = new(false, 0, 0, null, null, null);

    /// <summary>How a refresh is getting on, whether you started it or the timer did.</summary>
    public PriceRefreshProgress RefreshProgress => _refresh;

    /// <summary>
    /// Refreshes prices now, in the background, and reports whether it started.
    ///
    /// Returns false when one is already running rather than queueing a second: two
    /// concurrent sweeps would double the API load to reach the same answer, and the
    /// daily timer can be part-way through one at any moment.
    /// </summary>
    public bool StartRefresh()
    {
        lock (_refreshLock)
        {
            if (_refresh.Running) return false;
            _refresh = new PriceRefreshProgress(true, 0, 0, "Starting", null, null);
        }

        _ = Task.Run(async () =>
        {
            try { await CaptureAsync(CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "Manual price refresh failed"); }
        }, CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Fetches one card's real data in the background, after it has already been
    /// added to the collection.
    ///
    /// This is for cards added from the offline catalogue, which arrive with no price
    /// and a guessed printing because nothing touched the network. The add itself
    /// stays instant and cannot fail; this catches up a few seconds later. When it
    /// doesn't — the API being the API — the daily refresh gets it within the day,
    /// which is why there is deliberately no retry here.
    /// </summary>
    public void QueueEnrich(string cardId)
    {
        _ = Task.Run(async () =>
        {
            if (await EnrichOneAsync(cardId, CancellationToken.None))
                log.LogInformation("Filled in prices for newly added {CardId}", cardId);
        }, CancellationToken.None);
    }

    /// <summary>
    /// The same catch-up for a batch of cards, walked one at a time.
    ///
    /// A CSV import can commit a hundred catalogue-seeded cards in one go, and calling
    /// <see cref="QueueEnrich"/> per card would put a hundred simultaneous requests
    /// into an API that is unreliable under no load whatsoever. One sequential pass,
    /// paced the way the import itself paces its lookups, reaches the same answer
    /// without being the reason it fails.
    /// </summary>
    public void QueueEnrichMany(IReadOnlyList<string> cardIds)
    {
        if (cardIds.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var filled = 0;
            foreach (var cardId in cardIds)
            {
                if (await EnrichOneAsync(cardId, CancellationToken.None)) filled++;
                await Task.Delay(EnrichPacing, CancellationToken.None);
            }

            log.LogInformation(
                "Filled in prices for {Filled} of {Total} imported cards", filled, cardIds.Count);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Fetches and records one card, reporting whether it worked. Failure is logged at
    /// debug and otherwise swallowed: the daily refresh re-fetches every owned card,
    /// so there is nothing here worth retrying or surfacing.
    /// </summary>
    private async Task<bool> EnrichOneAsync(string cardId, CancellationToken ct)
    {
        try
        {
            var card = await api.GetCardAsync(cardId, ct);
            if (card is null) return false;

            cache.Upsert(card.Value);
            await RecordFromAllSourcesAsync(
                cardId, card.Value, DateTime.UtcNow.ToString("yyyy-MM-dd"), ct);
            return true;
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Could not fill in prices for {CardId} yet; the daily refresh will", cardId);
            return false;
        }
    }

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

        // Progress is tracked here rather than in the caller so the daily run reports
        // itself too — otherwise the UI would show "idle" while the timer was part-way
        // through a sweep, and pressing refresh would look like it did nothing.
        _refresh = new PriceRefreshProgress(true, 0, cardIds.Count, null, null, null);

        if (cardIds.Count == 0)
        {
            _refresh = _refresh with { Running = false, FinishedAt = DateTime.UtcNow.ToString("o") };
            return 0;
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var captured = 0;

        // Counted apart from `captured` so the progress bar measures how far through
        // the collection we are, not how many cards the API happened to answer for.
        // A run where pokemontcg.io refuses half the cards has still finished; a bar
        // stopping at 50% with no explanation just looks broken.
        var processed = 0;

        try
        {
        // One request per card. Batching with "id:a OR id:b" looks tempting but the
        // API 500s on those queries, and the per-card endpoint is reliable.
        foreach (var id in cardIds)
        {
            ct.ThrowIfCancellationRequested();

            // At the top, before any of the `continue`s below — a card we skip has
            // still been dealt with as far as progress is concerned.
            processed++;
            _refresh = _refresh with { Done = processed, Detail = id };

            if (CustomItemService.IsCustomId(id))
            {
                // Nothing to refresh from the catalogue — a custom item's payload is
                // whatever we stored when it was created. It's still worth pricing:
                // sources that go to a marketplace can put a live number on sealed
                // product and slabs, which is the only way those ever get one.
                var payload = cache.GetPayload(id);
                if (payload is null) continue;

                using var doc = JsonDocument.Parse(payload);
                await RecordFromAllSourcesAsync(id, doc.RootElement, today, ct);
                captured++;
            }
            else
            {
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
            }

            // Gentle pacing so a large collection doesn't trip the rate limit.
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return captured;
        }
        catch (Exception e)
        {
            _refresh = _refresh with { Error = e.Message };
            throw;
        }
        finally
        {
            // Fresh prices are exactly when a want can cross its target, so this is the
            // moment to work that out. In its own try: a want list that fails to update
            // is not a reason to report the capture itself as failed, and the next read
            // of the list recomputes it anyway.
            try
            {
                var crossed = services.GetRequiredService<WantsService>().RefreshAlerts();
                if (crossed.Count > 0)
                    log.LogInformation("{Count} wanted card(s) came down to your price", crossed.Count);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not refresh want-list alerts after the price capture");
            }

            // Always clears, so a failure can't leave the UI spinning forever or
            // block the next refresh from starting.
            _refresh = _refresh with
            {
                Running = false,
                // Says so plainly when the API refused some of them, rather than
                // leaving a short count to be puzzled over.
                Detail = captured < processed ? $"{captured} of {processed} refreshed" : null,
                FinishedAt = DateTime.UtcNow.ToString("o"),
            };
        }
    }

    /// <summary>
    /// Writes today's prices for a single card straight from what's already cached.
    ///
    /// Called when a card is added so its chart has a point immediately. Without
    /// this the first data point wouldn't appear until the next daily cycle, and a
    /// card added this morning would show an empty chart all day.
    /// </summary>
    public async Task RecordCurrentPricesAsync(string cardId, CancellationToken ct)
    {
        var payload = cache.GetPayload(cardId);
        if (payload is null) return;

        using var doc = JsonDocument.Parse(payload);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await RecordFromAllSourcesAsync(cardId, doc.RootElement, today, ct);
    }

    /// <summary>
    /// Blocking version, for the CSV import loop.
    ///
    /// Safe there and only there: import only ever adds catalogue cards, and every
    /// source that can price one reads the cached payload and completes without
    /// touching the network. A custom item would reach eBay and must not come
    /// through here — <see cref="RecordCurrentPricesAsync"/> is the one to call.
    /// </summary>
    public void RecordCurrentPrices(string cardId)
    {
        if (CustomItemService.IsCustomId(cardId))
        {
            log.LogWarning("Custom item {CardId} routed through the blocking price seed; skipping", cardId);
            return;
        }

        RecordCurrentPricesAsync(cardId, CancellationToken.None).GetAwaiter().GetResult();
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
    ///
    /// Custom items are included even though there's no catalogue entry behind them.
    /// Whether any source can actually price one is left to <see cref="IPriceSource.CanPrice"/>:
    /// the two catalogue sources decline them for want of a tcgplayer or cardmarket
    /// block in the payload, and eBay picks them up. Filtering them out here, as this
    /// used to, meant the marketplace source could never see the only items it exists
    /// to price.
    /// </summary>
    private List<string> OwnedCardIds()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT card_id FROM (
                SELECT c.card_id AS card_id FROM collection c
                UNION
                SELECT w.card_id FROM wants w
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
