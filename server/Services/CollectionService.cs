using System.Text.Json;
using PokemonVault.Data;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>Reads and writes the cards you own, and values them against cached prices.</summary>
public sealed class CollectionService(Db db)
{
    private const string SelectItems = """
        SELECT c.id, c.card_id, c.quantity, c.variant, c.condition, c.grade,
               c.purchase_price, c.purchase_date, c.notes, c.added_at,
               k.name, k.set_id, k.set_name, k.set_series, k.number, k.rarity,
               k.supertype, k.types, k.hp, k.artist, k.release_date,
               k.image_small, k.image_large, k.payload,
               c.manual_value, k.is_custom
        FROM collection c
        JOIN cards k ON k.id = c.card_id
        """;

    public List<CollectionItem> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectItems + " ORDER BY c.added_at DESC";

        var items = new List<CollectionItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(Map(r));
        return items;
    }

    public long Add(AddEntryRequest req)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collection (card_id, quantity, variant, condition, grade,
                                    purchase_price, purchase_date, notes, manual_value, added_at)
            VALUES ($cardId, $quantity, $variant, $condition, $grade,
                    $purchasePrice, $purchaseDate, $notes, $manualValue, $addedAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$cardId", req.CardId);
        cmd.Parameters.AddWithValue("$quantity", Math.Max(1, req.Quantity));
        cmd.Parameters.AddWithValue("$variant", req.Variant);
        cmd.Parameters.AddWithValue("$condition", req.Condition);
        cmd.Parameters.AddWithValue("$grade", (object?)req.Grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$purchasePrice", (object?)req.PurchasePrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$purchaseDate", (object?)req.PurchaseDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", (object?)req.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$manualValue", (object?)req.ManualValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("o"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool Update(long id, UpdateEntryRequest req)
    {
        // Only touch the fields the caller actually sent, so a partial edit from the
        // detail panel doesn't wipe out purchase info it never displayed.
        var sets = new List<string>();
        var pars = new Dictionary<string, object>();

        void Set(string col, string par, object? val)
        {
            if (val is null) return;
            sets.Add($"{col} = ${par}");
            pars[$"${par}"] = val;
        }

        Set("quantity", "quantity", req.Quantity is { } q ? Math.Max(1, q) : null);
        Set("variant", "variant", req.Variant);
        Set("condition", "condition", req.Condition);
        Set("grade", "grade", req.Grade);
        Set("purchase_price", "purchasePrice", req.PurchasePrice);
        Set("purchase_date", "purchaseDate", req.PurchaseDate);
        Set("notes", "notes", req.Notes);

        // Clearing needs an explicit flag: a null ManualValue means "leave alone",
        // otherwise you could never go back to tracking market price.
        if (req.ClearManualValue) sets.Add("manual_value = NULL");
        else Set("manual_value", "manualValue", req.ManualValue);

        if (sets.Count == 0) return true;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE collection SET {string.Join(", ", sets)} WHERE id = $id";
        foreach (var (k, v) in pars) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM collection WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public CollectionStats Stats()
    {
        var items = List();

        var bySet = items
            .GroupBy(i => (i.SetId, i.SetName))
            .Select(g => new SetBreakdown(g.Key.SetId, g.Key.SetName,
                g.Sum(i => i.Quantity), Math.Round(g.Sum(i => i.LineValue ?? 0), 2)))
            .OrderByDescending(s => s.Value)
            .ToList();

        // Value the same way the grid does, so a slab's own valuation counts here too.
        var best = items
            .Where(i => i.PurchasePrice is > 0 && (i.ManualValue ?? i.MarketPrice) is not null)
            .Select(i => (i.Name, Gain: ((i.ManualValue ?? i.MarketPrice ?? 0) - (i.PurchasePrice ?? 0)) * i.Quantity))
            .OrderByDescending(x => x.Gain)
            .FirstOrDefault();

        var (realised, proceeds, sold) = SalesTotals();

        return new CollectionStats(
            DistinctCards: items.Select(i => i.CardId).Distinct().Count(),
            TotalCards: items.Sum(i => i.Quantity),
            TotalMarketValue: Math.Round(items.Sum(i => i.LineValue ?? 0), 2),
            TotalPaid: Math.Round(items.Sum(i => (i.PurchasePrice ?? 0) * i.Quantity), 2),
            BiggestGainAmount: best.Name is null ? null : Math.Round(best.Gain, 2),
            BiggestGainCardName: best.Name,
            BySet: bySet,
            ValueHistory: ValueHistory(),
            RealisedGain: realised,
            SaleProceeds: proceeds,
            CardsSold: sold);
    }

    /// <summary>
    /// Sales totals, read straight from the table rather than through SalesService —
    /// that service depends on this one, so calling back into it would be circular.
    /// Fees apply to the sale as a whole, not per card.
    /// </summary>
    private (double Realised, double Proceeds, int Sold) SalesTotals()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        // Proceeds and quantity count every sale. Profit only counts sales where the
        // cost is known — without a purchase price, "profit" would just be the sale
        // price and would overstate how well you'd done.
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN purchase_price IS NOT NULL
                                  THEN sale_price * quantity - COALESCE(fees, 0) - purchase_price * quantity
                             END), 0) AS realised,
                COALESCE(SUM(sale_price * quantity - COALESCE(fees, 0)), 0) AS proceeds,
                COALESCE(SUM(quantity), 0) AS sold
            FROM sales
            """;

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0, 0);

        return (Math.Round(r.GetDouble(0), 2), Math.Round(r.GetDouble(1), 2), r.GetInt32(2));
    }

    /// <summary>
    /// Collection value per day, reconstructed from the price snapshots. Only days we
    /// actually captured show up, so this fills in as the app runs.
    /// </summary>
    public List<ValuePoint> ValueHistory()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.captured_on, SUM(p.market * c.quantity)
            FROM price_history p
            JOIN collection c ON c.card_id = p.card_id AND c.variant = p.variant
            WHERE p.market IS NOT NULL
            GROUP BY p.captured_on
            ORDER BY p.captured_on
            """;
        var points = new List<ValuePoint>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            points.Add(new ValuePoint(r.GetString(0), Math.Round(r.GetDouble(1), 2)));
        return points;
    }

    private static CollectionItem Map(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var payload = r.GetString(23);
        using var doc = JsonDocument.Parse(payload);
        var card = doc.RootElement;

        var variant = r.GetString(3);
        var quantity = r.GetInt32(2);
        var price = Pricing.ForVariant(card, variant);
        var manualValue = r.IsDBNull(24) ? (double?)null : r.GetDouble(24);
        var isCustom = !r.IsDBNull(25) && r.GetInt32(25) == 1;
        var types = r.IsDBNull(17)
            ? []
            : JsonSerializer.Deserialize<string[]>(r.GetString(17)) ?? [];

        // Your own number wins. A slabbed PSA 10 and a sealed booster box both have
        // a worth the catalogue's raw market price knows nothing about.
        var unitValue = manualValue ?? price.Market;

        return new CollectionItem(
            Id: r.GetInt64(0),
            CardId: r.GetString(1),
            Name: r.GetString(10),
            SetId: r.IsDBNull(11) ? null : r.GetString(11),
            SetName: r.IsDBNull(12) ? null : r.GetString(12),
            SetSeries: r.IsDBNull(13) ? null : r.GetString(13),
            Number: r.IsDBNull(14) ? null : r.GetString(14),
            Rarity: r.IsDBNull(15) ? null : r.GetString(15),
            Supertype: r.IsDBNull(16) ? null : r.GetString(16),
            Types: types,
            Hp: r.IsDBNull(18) ? null : r.GetString(18),
            Artist: r.IsDBNull(19) ? null : r.GetString(19),
            ReleaseDate: r.IsDBNull(20) ? null : r.GetString(20),
            ImageSmall: r.IsDBNull(21) ? null : r.GetString(21),
            ImageLarge: r.IsDBNull(22) ? null : r.GetString(22),
            Quantity: quantity,
            Variant: variant,
            Condition: r.GetString(4),
            Grade: r.IsDBNull(5) ? null : r.GetString(5),
            PurchasePrice: r.IsDBNull(6) ? null : r.GetDouble(6),
            PurchaseDate: r.IsDBNull(7) ? null : r.GetString(7),
            Notes: r.IsDBNull(8) ? null : r.GetString(8),
            AddedAt: r.GetString(9),
            MarketPrice: price.Market,
            LowPrice: price.Low,
            HighPrice: price.High,
            LineValue: unitValue is { } v ? Math.Round(v * quantity, 2) : null,
            PricesUpdatedAt: Pricing.TcgUpdatedAt(card),
            ManualValue: manualValue,
            IsCustom: isCustom);
    }
}
