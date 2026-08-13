using System.Text.Json;
using Microsoft.Data.Sqlite;
using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Set metadata and completion tracking. Sets are mirrored into SQLite so the
/// browser still works when pokemontcg.io is having one of its bad days, and so
/// completion percentages can be computed entirely from local data.
/// </summary>
public sealed class SetsService(Db db, PokemonTcgClient api, CardCache cache, ILogger<SetsService> log)
{
    /// <summary>
    /// All sets with your completion against each. Refreshes from the API when it
    /// can, and falls back to the cached copy when it can't.
    /// </summary>
    public async Task<List<SetSummary>> ListAsync(CancellationToken ct)
    {
        try
        {
            var res = await api.GetSetsAsync(ct);
            if (res.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                UpsertSets(data.EnumerateArray());
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not refresh sets; serving the cached list");
        }

        var owned = OwnedBySet();
        return CachedSets()
            .Select(s => s with
            {
                OwnedDistinct = owned.GetValueOrDefault(s.Id).Distinct,
                OwnedTotal = owned.GetValueOrDefault(s.Id).Total,
            })
            .ToList();
    }

    /// <summary>
    /// Every card in a set, flagged with how many you own. This is the binder page:
    /// the full checklist, not just what you have.
    /// </summary>
    public async Task<List<SetCard>> CardsInSetAsync(string setId, CancellationToken ct)
    {
        var payloads = cache.FindPayloads(setId, null, null, limit: 1000);

        // A partially-cached set would show phantom gaps, so only trust the cache
        // once it holds the whole thing.
        var expected = CachedSets().FirstOrDefault(s => s.Id == setId)?.Total ?? 0;
        if (payloads.Count == 0 || (expected > 0 && payloads.Count < expected))
        {
            payloads = await FetchSetAsync(setId, ct);
        }

        var owned = OwnedByCard();

        return payloads
            .Select(p => ToSetCard(p, owned))
            .OrderBy(c => c.NumberSort)
            .ThenBy(c => c.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> FetchSetAsync(string setId, CancellationToken ct)
    {
        var all = new List<JsonElement>();

        for (var page = 1; page <= 4; page++)
        {
            var res = await api.SearchCardsAsync($"set.id:{setId}", page, 250, ct);
            if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;

            var batch = data.EnumerateArray().Select(c => c.Clone()).ToList();
            all.AddRange(batch);

            var totalCount = res.TryGetProperty("totalCount", out var tc) ? tc.GetInt32() : all.Count;
            if (all.Count >= totalCount || batch.Count == 0) break;
        }

        if (all.Count > 0) cache.UpsertMany(all);
        return all.Select(c => c.GetRawText()).ToList();
    }

    // ------------------------------------------------------------------ storage

    private void UpsertSets(IEnumerable<JsonElement> sets)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        foreach (var set in sets)
        {
            var images = set.TryGetProperty("images", out var im) ? im : default;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sets (id, name, series, printed_total, total, release_date, logo, symbol, cached_at)
                VALUES ($id, $name, $series, $printedTotal, $total, $releaseDate, $logo, $symbol, $cachedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, series=excluded.series, printed_total=excluded.printed_total,
                    total=excluded.total, release_date=excluded.release_date,
                    logo=excluded.logo, symbol=excluded.symbol, cached_at=excluded.cached_at;
                """;
            cmd.Parameters.AddWithValue("$id", Text(set, "id") ?? "");
            cmd.Parameters.AddWithValue("$name", Text(set, "name") ?? "");
            cmd.Parameters.AddWithValue("$series", (object?)Text(set, "series") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$printedTotal", Number(set, "printedTotal"));
            cmd.Parameters.AddWithValue("$total", Number(set, "total"));
            cmd.Parameters.AddWithValue("$releaseDate", (object?)Text(set, "releaseDate") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$logo", (object?)Text(images, "logo") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$symbol", (object?)Text(images, "symbol") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cachedAt", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private List<SetSummary> CachedSets()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, series, printed_total, total, release_date, logo, symbol
            FROM sets ORDER BY release_date DESC
            """;

        var sets = new List<SetSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            sets.Add(new SetSummary(
                Id: r.GetString(0),
                Name: r.GetString(1),
                Series: r.IsDBNull(2) ? null : r.GetString(2),
                PrintedTotal: r.IsDBNull(3) ? 0 : r.GetInt32(3),
                Total: r.IsDBNull(4) ? 0 : r.GetInt32(4),
                ReleaseDate: r.IsDBNull(5) ? null : r.GetString(5),
                Logo: r.IsDBNull(6) ? null : r.GetString(6),
                Symbol: r.IsDBNull(7) ? null : r.GetString(7),
                OwnedDistinct: 0,
                OwnedTotal: 0));
        }
        return sets;
    }

    /// <summary>Distinct cards and total copies owned, keyed by set.</summary>
    private Dictionary<string, (int Distinct, int Total)> OwnedBySet()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT k.set_id, COUNT(DISTINCT c.card_id), COALESCE(SUM(c.quantity), 0)
            FROM collection c
            JOIN cards k ON k.id = c.card_id
            WHERE k.set_id IS NOT NULL
            GROUP BY k.set_id
            """;

        var map = new Dictionary<string, (int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
        return map;
    }

    private Dictionary<string, int> OwnedByCard()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT card_id, COALESCE(SUM(quantity), 0) FROM collection GROUP BY card_id";

        var map = new Dictionary<string, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    private static SetCard ToSetCard(string payload, IReadOnlyDictionary<string, int> owned)
    {
        using var doc = JsonDocument.Parse(payload);
        var card = doc.RootElement;
        var images = card.TryGetProperty("images", out var im) ? im : default;
        var price = Pricing.ForVariant(card, null);
        var id = Text(card, "id") ?? "";
        var number = Text(card, "number") ?? "";

        return new SetCard(
            CardId: id,
            Name: Text(card, "name") ?? "",
            Number: number,
            NumberSort: LeadingNumber(number),
            Rarity: Text(card, "rarity"),
            Supertype: Text(card, "supertype"),
            ImageSmall: Text(images, "small"),
            MarketPrice: price.Market,
            Variants: Pricing.AvailableVariants(card),
            OwnedQuantity: owned.GetValueOrDefault(id));
    }

    /// <summary>
    /// Card numbers aren't plain integers — "4", "SV49" and "TG12" all occur — so
    /// sort on the leading digits and fall back to text for the rest.
    /// </summary>
    private static int LeadingNumber(string number)
    {
        var digits = new string(number.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    private static string? Text(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    private static object Number(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : DBNull.Value;
}
