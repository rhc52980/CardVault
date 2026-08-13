using Microsoft.Data.Sqlite;
using PokemonVault.Data;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>
/// The ledger of cards you've parted with.
///
/// Deleting an entry used to erase the fact you ever owned it, which meant every
/// gain the app reported was unrealised — a collection that had funded itself
/// through trades looked identical to one that never sold a thing. Selling records
/// what left, for how much, and what it cost you.
/// </summary>
public sealed class SalesService(Db db, CollectionService collection)
{
    /// <summary>
    /// Records a sale and takes the cards out of the collection. Selling fewer than
    /// you own leaves the rest of the entry in place.
    /// </summary>
    public (bool Ok, string? Error, long SaleId) Sell(long entryId, SellRequest req)
    {
        var entry = collection.List().FirstOrDefault(i => i.Id == entryId);
        if (entry is null) return (false, "That entry no longer exists.", 0);

        var quantity = Math.Max(1, req.Quantity);
        if (quantity > entry.Quantity)
            return (false, $"You only have {entry.Quantity} of those.", 0);

        if (req.SalePrice < 0) return (false, "Sale price can't be negative.", 0);

        var saleDate = string.IsNullOrWhiteSpace(req.SaleDate)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : req.SaleDate.Trim();

        long saleId;
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO sales (card_id, card_name, set_name, number, image_small, quantity,
                                   variant, condition, grade, purchase_price, sale_price, fees,
                                   sale_date, notes, recorded_at)
                VALUES ($cardId, $cardName, $setName, $number, $imageSmall, $quantity,
                        $variant, $condition, $grade, $purchasePrice, $salePrice, $fees,
                        $saleDate, $notes, $recordedAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$cardId", entry.CardId);
            cmd.Parameters.AddWithValue("$cardName", entry.Name);
            cmd.Parameters.AddWithValue("$setName", (object?)entry.SetName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$number", (object?)entry.Number ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imageSmall", (object?)entry.ImageSmall ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$quantity", quantity);
            cmd.Parameters.AddWithValue("$variant", entry.Variant);
            cmd.Parameters.AddWithValue("$condition", entry.Condition);
            cmd.Parameters.AddWithValue("$grade", (object?)entry.Grade ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$purchasePrice", (object?)entry.PurchasePrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$salePrice", req.SalePrice);
            cmd.Parameters.AddWithValue("$fees", (object?)req.Fees ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$saleDate", saleDate);
            cmd.Parameters.AddWithValue("$notes", (object?)req.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$recordedAt", DateTime.UtcNow.ToString("o"));
            saleId = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        // Remove what was sold. A partial sale keeps the remainder.
        if (quantity >= entry.Quantity) collection.Delete(entryId);
        else collection.Update(entryId, new UpdateEntryRequest(Quantity: entry.Quantity - quantity));

        return (true, null, saleId);
    }

    public List<SaleRecord> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, card_id, card_name, set_name, number, image_small, quantity, variant,
                   condition, grade, purchase_price, sale_price, fees, sale_date, notes, recorded_at
            FROM sales
            ORDER BY sale_date DESC, id DESC
            """;

        var sales = new List<SaleRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) sales.Add(Map(r));
        return sales;
    }

    public bool Delete(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sales WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Totals for the stats bar: proceeds after fees, and profit over cost.</summary>
    public (double RealisedGain, double Proceeds, int CardsSold) Totals()
    {
        var sales = List();
        return (
            Math.Round(sales.Sum(s => s.RealisedGain ?? 0), 2),
            Math.Round(sales.Sum(s => s.Proceeds), 2),
            sales.Sum(s => s.Quantity));
    }

    private static SaleRecord Map(SqliteDataReader r)
    {
        var quantity = r.GetInt32(6);
        var purchasePrice = r.IsDBNull(10) ? (double?)null : r.GetDouble(10);
        var salePrice = r.GetDouble(11);
        var fees = r.IsDBNull(12) ? (double?)null : r.GetDouble(12);

        // Fees are for the sale as a whole, not per card.
        var proceeds = salePrice * quantity - (fees ?? 0);
        var gain = purchasePrice is { } cost ? proceeds - cost * quantity : (double?)null;

        return new SaleRecord(
            Id: r.GetInt64(0),
            CardId: r.GetString(1),
            CardName: r.GetString(2),
            SetName: r.IsDBNull(3) ? null : r.GetString(3),
            Number: r.IsDBNull(4) ? null : r.GetString(4),
            ImageSmall: r.IsDBNull(5) ? null : r.GetString(5),
            Quantity: quantity,
            Variant: r.IsDBNull(7) ? null : r.GetString(7),
            Condition: r.IsDBNull(8) ? null : r.GetString(8),
            Grade: r.IsDBNull(9) ? null : r.GetString(9),
            PurchasePrice: purchasePrice,
            SalePrice: salePrice,
            Fees: fees,
            SaleDate: r.GetString(13),
            Notes: r.IsDBNull(14) ? null : r.GetString(14),
            RecordedAt: r.GetString(15),
            RealisedGain: gain is { } g ? Math.Round(g, 2) : null,
            Proceeds: Math.Round(proceeds, 2));
    }
}
