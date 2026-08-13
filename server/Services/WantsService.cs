using System.Text.Json;
using Microsoft.Data.Sqlite;
using PokemonVault.Data;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>
/// Cards you're hunting for, and the price you'd pay.
///
/// The set browser already shows the gaps in a set; this is the cross-set version —
/// the specific cards you're actually chasing. Because the app records prices daily
/// anyway, it can tell you when one of them comes down to your number.
/// </summary>
public sealed class WantsService(Db db, CollectionService collection)
{
    public List<WantItem> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT w.id, w.card_id, w.variant, w.target_price, w.quantity, w.notes, w.added_at,
                   k.name, k.set_name, k.number, k.rarity, k.image_small, k.payload
            FROM wants w
            JOIN cards k ON k.id = w.card_id
            ORDER BY w.added_at DESC
            """;

        var items = new List<WantItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(Map(r));

        // Cards that have hit your price float to the top — that's the reason to look.
        return items
            .OrderByDescending(i => i.AtOrBelowTarget)
            .ThenBy(i => i.DifferenceToTarget ?? double.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Adds a want, or updates the existing one for that card and printing. The
    /// unique constraint means you can't end up with the same card twice.
    /// </summary>
    public long Add(AddWantRequest req)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wants (card_id, variant, target_price, quantity, notes, added_at)
            VALUES ($cardId, $variant, $targetPrice, $quantity, $notes, $addedAt)
            ON CONFLICT(card_id, variant) DO UPDATE SET
                target_price = excluded.target_price,
                quantity     = excluded.quantity,
                notes        = excluded.notes;
            SELECT id FROM wants WHERE card_id = $cardId AND variant = $variant;
            """;
        cmd.Parameters.AddWithValue("$cardId", req.CardId);
        cmd.Parameters.AddWithValue("$variant", req.Variant);
        cmd.Parameters.AddWithValue("$targetPrice", (object?)req.TargetPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quantity", Math.Max(1, req.Quantity));
        cmd.Parameters.AddWithValue("$notes", (object?)req.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("o"));
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public bool Update(long id, UpdateWantRequest req)
    {
        var sets = new List<string>();
        var pars = new Dictionary<string, object>();

        void Set(string col, string par, object? val)
        {
            if (val is null) return;
            sets.Add($"{col} = ${par}");
            pars[$"${par}"] = val;
        }

        Set("quantity", "quantity", req.Quantity is { } q ? Math.Max(1, q) : null);
        Set("notes", "notes", req.Notes);
        Set("variant", "variant", req.Variant);

        if (req.ClearTarget) sets.Add("target_price = NULL");
        else Set("target_price", "targetPrice", req.TargetPrice);

        if (sets.Count == 0) return true;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE wants SET {string.Join(", ", sets)} WHERE id = $id";
        foreach (var (k, v) in pars) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM wants WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// You found one. Moves the want into the collection and takes it off the list,
    /// carrying across the printing you were after.
    /// </summary>
    public (bool Ok, string? Error, long EntryId) Acquire(long id, AddEntryRequest req)
    {
        var want = List().FirstOrDefault(w => w.Id == id);
        if (want is null) return (false, "That want no longer exists.", 0);

        var entryId = collection.Add(req with
        {
            CardId = want.CardId,
            Variant = string.IsNullOrWhiteSpace(req.Variant) ? want.Variant : req.Variant,
        });

        Delete(id);
        return (true, null, entryId);
    }

    /// <summary>Card ids on the want list, so price refreshes cover them too.</summary>
    public List<string> WantedCardIds()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT card_id FROM wants";
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private WantItem Map(SqliteDataReader r)
    {
        var variant = r.GetString(2);
        var target = r.IsDBNull(3) ? (double?)null : r.GetDouble(3);

        using var doc = JsonDocument.Parse(r.GetString(12));
        var card = doc.RootElement;
        var market = Pricing.ForVariant(card, variant).Market;

        var difference = target is { } t && market is { } m ? m - t : (double?)null;

        return new WantItem(
            Id: r.GetInt64(0),
            CardId: r.GetString(1),
            Name: r.GetString(7),
            SetName: r.IsDBNull(8) ? null : r.GetString(8),
            Number: r.IsDBNull(9) ? null : r.GetString(9),
            Rarity: r.IsDBNull(10) ? null : r.GetString(10),
            ImageSmall: r.IsDBNull(11) ? null : r.GetString(11),
            Variant: variant,
            Variants: Pricing.AvailableVariants(card),
            Quantity: r.GetInt32(4),
            TargetPrice: target,
            Notes: r.IsDBNull(5) ? null : r.GetString(5),
            AddedAt: r.GetString(6),
            MarketPrice: market,
            DifferenceToTarget: difference is { } d ? Math.Round(d, 2) : null,
            AtOrBelowTarget: difference is <= 0);
    }

    /// <summary>Every card id on the list, for badging search and set-browser results.</summary>
    public HashSet<string> WantedIds() => [.. WantedCardIds()];
}
