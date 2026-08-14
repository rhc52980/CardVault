using System.Globalization;
using System.Text;
using System.Text.Json;
using CardVault.Data;

namespace CardVault.Services;

/// <summary>
/// Gets your collection back out of the app.
///
/// Two formats on purpose. The CSV uses exactly the column names the importer
/// understands, so an export can be re-imported — into this app, a spreadsheet, or
/// whatever you move to next. The JSON is the complete picture including sales
/// history and hand-entered items, for when you want nothing lost.
/// </summary>
public sealed class ExportService(CollectionService collection, SalesService sales, Db db)
{
    /// <summary>
    /// Column headers deliberately match the importer's aliases so a round trip
    /// works without editing anything.
    /// </summary>
    public string CollectionCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Card ID", "Name", "Set", "Number", "Quantity", "Variant", "Condition", "Grade",
            "Purchase Price", "Purchase Date", "Location", "Notes", "Market Price", "Your Value",
            "Line Value", "Rarity", "Hand Entered"));

        foreach (var i in collection.List().OrderBy(i => i.SetName).ThenBy(i => i.Name))
        {
            sb.AppendLine(string.Join(",",
                Q(i.CardId), Q(i.Name), Q(i.SetName), Q(i.Number), N(i.Quantity), Q(i.Variant),
                Q(i.Condition), Q(i.Grade), N(i.PurchasePrice), Q(i.PurchaseDate), Q(i.Location),
                Q(i.Notes), N(i.MarketPrice), N(i.ManualValue), N(i.LineValue), Q(i.Rarity),
                i.IsCustom ? "yes" : "no"));
        }

        return sb.ToString();
    }

    public string SalesCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Sale Date", "Name", "Set", "Number", "Quantity", "Variant", "Condition", "Grade",
            "Purchase Price", "Sale Price", "Fees", "Proceeds", "Realised Gain", "Notes"));

        foreach (var s in sales.List())
        {
            sb.AppendLine(string.Join(",",
                Q(s.SaleDate), Q(s.CardName), Q(s.SetName), Q(s.Number), N(s.Quantity), Q(s.Variant),
                Q(s.Condition), Q(s.Grade), N(s.PurchasePrice), N(s.SalePrice), N(s.Fees),
                N(s.Proceeds), N(s.RealisedGain), Q(s.Notes)));
        }

        return sb.ToString();
    }

    /// <summary>Everything, in one file — collection, sales and the totals.</summary>
    public string EverythingJson()
    {
        var payload = new
        {
            exportedAt = DateTime.UtcNow.ToString("o"),
            format = 1,
            note = "Hand-entered items have ids beginning 'custom-' that only mean something "
                   + "in the vault that created them.",
            stats = collection.Stats(),
            collection = collection.List(),
            sales = sales.List(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    /// <summary>
    /// The collection as something you could actually build a deck from — meant to
    /// be handed to an AI, or read by anything else that wants to reason about play
    /// rather than about value.
    ///
    /// This is a different shape from the full export on purpose. That one answers
    /// "what do I own and what is it worth", so it carries prices, purchase history,
    /// condition and images, and none of that has any bearing on whether a deck
    /// works. Meanwhile the things that decide whether a deck is even legal — energy
    /// costs, what a card evolves from, retreat cost, format legality — were absent
    /// from it despite sitting in the cached payload all along.
    ///
    /// Dropping the former roughly pays for adding the latter, which matters because
    /// the whole point is to fit inside a model's context.
    ///
    /// Copies are totalled per card rather than per entry: three Rare Candy across
    /// two rows is three Rare Candy to a deck, and the four-of rule counts cards, not
    /// rows. Hand-entered items are left out — a sealed booster box is not a card you
    /// can put in a deck.
    /// </summary>
    /// <param name="format">"standard" or "expanded" to keep only cards legal there.</param>
    public string DeckInventoryJson(string? format = null)
    {
        var wanted = format?.Trim().ToLowerInvariant();
        var cards = new List<object>();

        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT k.payload, SUM(c.quantity) AS owned
                FROM collection c
                JOIN cards k ON k.id = c.card_id
                WHERE COALESCE(k.is_custom, 0) = 0 AND k.payload IS NOT NULL
                GROUP BY c.card_id
                ORDER BY k.name
                """;

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                using var doc = JsonDocument.Parse(r.GetString(0));
                var card = doc.RootElement;

                if (wanted is "standard" or "expanded" && Legality(card, wanted) != "Legal") continue;

                cards.Add(new
                {
                    id = Str(card, "id"),
                    name = Str(card, "name"),
                    owned = r.GetInt32(1),
                    supertype = Str(card, "supertype"),
                    subtypes = Strings(card, "subtypes"),
                    types = Strings(card, "types"),
                    hp = Str(card, "hp"),
                    evolvesFrom = Str(card, "evolvesFrom"),
                    // The API gives retreat as one energy symbol per element; the count
                    // is the only part a deck cares about.
                    retreat = card.TryGetProperty("retreatCost", out var rc) && rc.ValueKind == JsonValueKind.Array
                        ? rc.GetArrayLength()
                        : 0,
                    attacks = Attacks(card),
                    abilities = Abilities(card),
                    rarity = Str(card, "rarity"),
                    set = new { id = SetStr(card, "id"), name = SetStr(card, "name") },
                    number = Str(card, "number"),
                    legal = new { standard = Legality(card, "standard"), expanded = Legality(card, "expanded") },
                });
            }
        }

        var payload = new
        {
            exportedAt = DateTime.UtcNow.ToString("o"),
            format = wanted is "standard" or "expanded" ? wanted : "all",
            note = "Playable cards only, with copies totalled per card. 'owned' is how many you "
                   + "have. Deck rules: 60 cards, at most 4 of any one name, unlimited basic Energy.",
            cards,
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static object[] Attacks(JsonElement card)
        => card.TryGetProperty("attacks", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Select(x => (object)new
                {
                    name = Str(x, "name"),
                    cost = Strings(x, "cost"),
                    damage = Str(x, "damage"),
                    text = Str(x, "text"),
                })
                .ToArray()
            : [];

    private static object[] Abilities(JsonElement card)
        => card.TryGetProperty("abilities", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Select(x => (object)new { name = Str(x, "name"), type = Str(x, "type"), text = Str(x, "text") })
                .ToArray()
            : [];

    private static string? Legality(JsonElement card, string format)
        => card.TryGetProperty("legalities", out var l) && l.ValueKind == JsonValueKind.Object
            ? Str(l, format)
            : null;

    private static string? SetStr(JsonElement card, string prop)
        => card.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, prop) : null;

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    private static string[] Strings(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    /// <summary>Quotes a CSV field, doubling any embedded quotes.</summary>
    private static string Q(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string N(double? value)
        => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
