using System.Text.Json;
using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Decks you're building, and what you'd still have to find to play them.
///
/// A deck lists cards, not the copies you own. That separation is the whole point:
/// the interesting question is the gap between the two, so a deck has to be able to
/// name a card you haven't got, and has to survive you selling the copy you had.
///
/// Rules are reported, never enforced. A deck of 43 cards with five Pikachu in it is
/// a deck in progress, and refusing to save it would make the app useless for the
/// thing people actually do — build towards a list over weeks.
/// </summary>
public sealed class DeckService(Db db)
{
    public List<DeckSummary> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        // Owned is summed per card across the whole collection, so a card sitting in
        // two decks counts as available to both. That matches how paper decks work --
        // you build one at a time and move cards between them.
        cmd.CommandText = """
            SELECT d.id, d.name, d.format, d.notes, d.created_at, d.updated_at,
                   COALESCE(SUM(dc.quantity), 0) AS cards,
                   COALESCE(SUM(MAX(0, dc.quantity - COALESCE(owned.n, 0))), 0) AS short
            FROM decks d
            LEFT JOIN deck_cards dc ON dc.deck_id = d.id
            LEFT JOIN (SELECT card_id, SUM(quantity) AS n FROM collection GROUP BY card_id) owned
                   ON owned.card_id = dc.card_id
            GROUP BY d.id
            ORDER BY d.name COLLATE NOCASE
            """;

        var decks = new List<DeckSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            decks.Add(new DeckSummary(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                Format: r.GetString(2),
                Notes: r.IsDBNull(3) ? null : r.GetString(3),
                CreatedAt: r.GetString(4),
                UpdatedAt: r.IsDBNull(5) ? null : r.GetString(5),
                Cards: r.GetInt32(6),
                Missing: r.GetInt32(7)));

        return decks;
    }

    public Deck? Get(long id)
    {
        var summary = List().FirstOrDefault(d => d.Id == id);
        if (summary is null) return null;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dc.card_id, dc.quantity, k.name, k.set_name, k.number, k.rarity,
                   k.supertype, k.image_small, k.payload,
                   COALESCE((SELECT SUM(quantity) FROM collection c WHERE c.card_id = dc.card_id), 0)
            FROM deck_cards dc
            JOIN cards k ON k.id = dc.card_id
            WHERE dc.deck_id = $id
            ORDER BY k.supertype, k.name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$id", id);

        var cards = new List<DeckCard>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                using var doc = JsonDocument.Parse(r.GetString(8));
                var payload = doc.RootElement;

                var needed = r.GetInt32(1);
                var owned = r.GetInt32(9);
                var basicEnergy = Formats.IsBasicEnergy(payload);

                cards.Add(new DeckCard(
                    CardId: r.GetString(0),
                    Name: r.GetString(2),
                    SetName: r.IsDBNull(3) ? null : r.GetString(3),
                    Number: r.IsDBNull(4) ? null : r.GetString(4),
                    Rarity: r.IsDBNull(5) ? null : r.GetString(5),
                    Supertype: r.IsDBNull(6) ? null : r.GetString(6),
                    ImageSmall: r.IsDBNull(7) ? null : r.GetString(7),
                    Needed: needed,
                    Owned: owned,
                    Short: Math.Max(0, needed - owned),
                    Legal: Formats.IsLegal(payload, summary.Format),
                    BasicEnergy: basicEnergy,
                    // Basic Energy is the one card you may hold any number of, so the
                    // four-copy rule simply doesn't apply to it.
                    OverCopyLimit: !basicEnergy && needed > Formats.CopyLimit));
            }
        }

        return new Deck(summary, cards, Problems(summary, cards));
    }

    /// <summary>
    /// What stops this being a playable list. Reported rather than enforced — a deck
    /// under construction is not a mistake, and an app that refused to save one would
    /// be no use for the thing people actually do.
    /// </summary>
    private static List<string> Problems(DeckSummary deck, List<DeckCard> cards)
    {
        var problems = new List<string>();
        var total = cards.Sum(c => c.Needed);

        if (deck.Format == Formats.Unlimited) return problems;

        if (total != Formats.DeckSize)
            problems.Add(total < Formats.DeckSize
                ? $"{Formats.DeckSize - total} cards short of {Formats.DeckSize}."
                : $"{total - Formats.DeckSize} cards over {Formats.DeckSize}.");

        foreach (var card in cards.Where(c => c.OverCopyLimit))
            problems.Add($"{card.Needed} copies of {card.Name} — the limit is {Formats.CopyLimit}.");

        foreach (var card in cards.Where(c => !c.Legal))
            problems.Add($"{card.Name} isn't legal in {Formats.Name(deck.Format)}.");

        return problems;
    }

    public long Create(DeckRequest req)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decks (name, format, notes, created_at)
            VALUES ($name, $format, $notes, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", Name(req.Name));
        cmd.Parameters.AddWithValue("$format", Formats.Normalize(req.Format));
        cmd.Parameters.AddWithValue("$notes", (object?)req.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool Update(long id, DeckRequest req)
    {
        var sets = new List<string> { "updated_at = $now" };
        var pars = new Dictionary<string, object> { ["$now"] = DateTime.UtcNow.ToString("o") };

        if (req.Name is not null) { sets.Add("name = $name"); pars["$name"] = Name(req.Name); }
        if (req.Format is not null) { sets.Add("format = $format"); pars["$format"] = Formats.Normalize(req.Format); }
        if (req.Notes is not null) { sets.Add("notes = $notes"); pars["$notes"] = req.Notes; }

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE decks SET {string.Join(", ", sets)} WHERE id = $id";
        foreach (var (k, v) in pars) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var conn = db.Open();

        // The foreign key names ON DELETE CASCADE, but SQLite only honours it when
        // enforcement is switched on for the connection. Clearing the list explicitly
        // means the deck's cards go with it either way.
        using (var cards = conn.CreateCommand())
        {
            cards.CommandText = "DELETE FROM deck_cards WHERE deck_id = $id";
            cards.Parameters.AddWithValue("$id", id);
            cards.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM decks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Sets how many of a card the deck calls for. Zero removes it, which is the same
    /// gesture as decrementing the last one and saves needing a separate verb.
    /// </summary>
    public bool SetCard(long deckId, string cardId, int quantity)
    {
        using var conn = db.Open();

        using (var touch = conn.CreateCommand())
        {
            touch.CommandText = "UPDATE decks SET updated_at = $now WHERE id = $id";
            touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            touch.Parameters.AddWithValue("$id", deckId);
            if (touch.ExecuteNonQuery() == 0) return false;
        }

        using var cmd = conn.CreateCommand();
        if (quantity <= 0)
        {
            cmd.CommandText = "DELETE FROM deck_cards WHERE deck_id = $deck AND card_id = $card";
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO deck_cards (deck_id, card_id, quantity) VALUES ($deck, $card, $qty)
                ON CONFLICT(deck_id, card_id) DO UPDATE SET quantity = excluded.quantity
                """;
            cmd.Parameters.AddWithValue("$qty", quantity);
        }

        cmd.Parameters.AddWithValue("$deck", deckId);
        cmd.Parameters.AddWithValue("$card", cardId);
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>Named so a deck is findable later; blank gets a placeholder, not a blank.</summary>
    private static string Name(string? raw)
    {
        var name = raw?.Trim() ?? "";
        if (name.Length == 0) return "Untitled deck";
        return name.Length > 80 ? name[..80].TrimEnd() : name;
    }
}
