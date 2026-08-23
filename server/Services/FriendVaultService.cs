using System.Text.Json;
using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Swapping collections with someone by file, rather than by opening a door.
///
/// The obvious alternative was a share link — a URL your friend opens against your
/// server. This is better for a vault that mostly isn't on the internet: nothing
/// becomes reachable, there's no token to leak into a chat window or a screenshot,
/// and it works when the server is only on your own network.
///
/// A share file deliberately carries no money. No purchase prices, no market values,
/// no locations, notes, photographs or review flags — just which cards, how many, and
/// what condition. Values would drag in the currency question and tell a trading
/// partner what your collection is worth; neither is any of their business, and
/// leaving the fields out of <see cref="SharedCard"/> makes the leak impossible to
/// write rather than something a filter has to remember.
/// </summary>
public sealed class FriendVaultService(Db db)
{
    /// <summary>Bumped only if the shape changes in a way an older reader can't handle.</summary>
    private const int FormatVersion = 1;

    // ------------------------------------------------------------------ exporting

    /// <summary>
    /// Your collection and want list, reduced to what a trading partner needs.
    /// </summary>
    public SharedVault Export(string vaultName)
    {
        using var conn = db.Open();

        var owned = new List<SharedCard>();
        using (var cmd = conn.CreateCommand())
        {
            // Grouped by card, printing and condition rather than listed per entry:
            // which of your three copies is which is your business, and a partner only
            // needs to know you have three.
            cmd.CommandText = """
                SELECT c.card_id, k.name, k.set_name, k.number, k.rarity,
                       c.condition, c.language, SUM(c.quantity)
                FROM collection c
                JOIN cards k ON k.id = c.card_id
                WHERE COALESCE(k.is_custom, 0) = 0
                GROUP BY c.card_id, c.condition, c.language
                ORDER BY k.name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) owned.Add(Read(r));
        }

        var wanted = new List<SharedCard>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT w.card_id, k.name, k.set_name, k.number, k.rarity,
                       NULL, NULL, w.quantity
                FROM wants w
                JOIN cards k ON k.id = w.card_id
                ORDER BY k.name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) wanted.Add(Read(r));
        }

        return new SharedVault(FormatVersion, vaultName, DateTime.UtcNow.ToString("o"), owned, wanted);
    }

    private static SharedCard Read(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(
            CardId: r.GetString(0),
            Name: r.GetString(1),
            SetName: r.IsDBNull(2) ? null : r.GetString(2),
            Number: r.IsDBNull(3) ? null : r.GetString(3),
            Rarity: r.IsDBNull(4) ? null : r.GetString(4),
            Condition: r.IsDBNull(5) ? null : r.GetString(5),
            Language: r.IsDBNull(6) ? null : r.GetString(6),
            Quantity: r.GetInt32(7));

    // ------------------------------------------------------------------ importing

    /// <summary>
    /// Stores a file someone sent you as a vault of its own.
    ///
    /// Nothing is merged. Their cards never enter your collection, so importing can't
    /// change what you're said to own or be worth, and removing it later is a delete
    /// rather than an unpick.
    /// </summary>
    public (bool Ok, string? Error, long Id) Import(string json, string? nameOverride)
    {
        SharedVault? shared;
        try
        {
            shared = JsonSerializer.Deserialize<SharedVault>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return (false, "That file isn't a vault export CardVault can read.", 0);
        }

        if (shared is null) return (false, "That file was empty.", 0);
        if (shared.Format > FormatVersion)
            return (false, $"That file was written by a newer CardVault (format {shared.Format}).", 0);
        if (shared.Owned.Count == 0 && shared.Wanted.Count == 0)
            return (false, "That vault has no cards in it.", 0);

        var name = (nameOverride ?? shared.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) name = "A friend";
        if (name.Length > 60) name = name[..60].TrimEnd();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO friend_vaults (name, exported_at, imported_at)
                VALUES ($name, $exported, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$exported", (object?)shared.ExportedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            id = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        foreach (var (kind, cards) in new[] { ("own", shared.Owned), ("want", shared.Wanted) })
            foreach (var card in cards)
            {
                if (string.IsNullOrWhiteSpace(card.CardId)) continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO friend_cards
                        (vault_id, kind, card_id, name, set_name, number, rarity, condition, language, quantity)
                    VALUES ($v, $kind, $card, $name, $set, $number, $rarity, $condition, $language, $qty)
                    """;
                cmd.Parameters.AddWithValue("$v", id);
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$card", card.CardId);
                cmd.Parameters.AddWithValue("$name", card.Name ?? card.CardId);
                cmd.Parameters.AddWithValue("$set", (object?)card.SetName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$number", (object?)card.Number ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rarity", (object?)card.Rarity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$condition", (object?)card.Condition ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$language", (object?)card.Language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$qty", Math.Max(1, card.Quantity));
                cmd.ExecuteNonQuery();
            }

        tx.Commit();
        return (true, null, id);
    }

    public List<FriendVaultSummary> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.name, v.exported_at, v.imported_at,
                   COALESCE(SUM(CASE WHEN f.kind = 'own' THEN f.quantity END), 0),
                   COALESCE(SUM(CASE WHEN f.kind = 'want' THEN f.quantity END), 0)
            FROM friend_vaults v
            LEFT JOIN friend_cards f ON f.vault_id = v.id
            GROUP BY v.id
            ORDER BY v.imported_at DESC
            """;

        var vaults = new List<FriendVaultSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            vaults.Add(new FriendVaultSummary(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                ExportedAt: r.IsDBNull(2) ? null : r.GetString(2),
                ImportedAt: r.GetString(3),
                Owned: r.GetInt32(4),
                Wanted: r.GetInt32(5)));

        return vaults;
    }

    public bool Delete(long id)
    {
        using var conn = db.Open();
        using (var cards = conn.CreateCommand())
        {
            cards.CommandText = "DELETE FROM friend_cards WHERE vault_id = $id";
            cards.Parameters.AddWithValue("$id", id);
            cards.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM friend_vaults WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ------------------------------------------------------------------- matching

    /// <summary>
    /// The point of importing someone's vault: what you could ask them for, and what
    /// you could offer.
    ///
    /// "Spare" means owning more than one. A single copy is the one in your binder,
    /// and offering it to somebody is a decision rather than an inventory fact.
    /// </summary>
    public FriendMatches? Matches(long id)
    {
        if (List().FirstOrDefault(v => v.Id == id) is not { } summary) return null;

        using var conn = db.Open();

        var theyHave = new List<TradeMatch>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.card_id, f.name, f.set_name, f.number, f.rarity, f.condition,
                       f.language, f.quantity, w.quantity
                FROM friend_cards f
                JOIN wants w ON w.card_id = f.card_id
                WHERE f.vault_id = $id AND f.kind = 'own'
                ORDER BY f.name COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) theyHave.Add(Match(r));
        }

        var youCouldOffer = new List<TradeMatch>();
        using (var cmd = conn.CreateCommand())
        {
            // Their want against your spares. Quantity reported is what you could give
            // up without emptying the slot, not everything you own.
            cmd.CommandText = """
                SELECT f.card_id, f.name, f.set_name, f.number, f.rarity, NULL, NULL,
                       f.quantity, SUM(c.quantity) - 1
                FROM friend_cards f
                JOIN collection c ON c.card_id = f.card_id
                WHERE f.vault_id = $id AND f.kind = 'want'
                GROUP BY f.card_id
                HAVING SUM(c.quantity) > 1
                ORDER BY f.name COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) youCouldOffer.Add(Match(r));
        }

        return new FriendMatches(summary, theyHave, youCouldOffer);
    }

    private static TradeMatch Match(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(
            CardId: r.GetString(0),
            Name: r.GetString(1),
            SetName: r.IsDBNull(2) ? null : r.GetString(2),
            Number: r.IsDBNull(3) ? null : r.GetString(3),
            Rarity: r.IsDBNull(4) ? null : r.GetString(4),
            Condition: r.IsDBNull(5) ? null : r.GetString(5),
            Language: r.IsDBNull(6) ? null : r.GetString(6),
            TheirQuantity: r.GetInt32(7),
            YourQuantity: r.IsDBNull(8) ? 0 : r.GetInt32(8));

    /// <summary>Everything in one friend's vault, for browsing rather than matching.</summary>
    public List<SharedCard> Cards(long id, string kind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT card_id, name, set_name, number, rarity, condition, language, quantity
            FROM friend_cards
            WHERE vault_id = $id AND kind = $kind
            ORDER BY name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind == "want" ? "want" : "own");

        var cards = new List<SharedCard>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) cards.Add(Read(r));
        return cards;
    }
}
