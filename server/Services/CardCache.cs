using System.Text.Json;
using Microsoft.Data.Sqlite;
using CardVault.Data;

namespace CardVault.Services;

/// <summary>
/// Local mirror of card metadata. Every card we see from the API gets stored here,
/// so the collection view renders instantly and works offline, and so a big
/// collection doesn't burn through the API rate limit on every page load.
/// </summary>
public sealed class CardCache(Db db)
{
    public void Upsert(JsonElement card)
    {
        using var conn = db.Open();
        Upsert(conn, card, null);
    }

    public void UpsertMany(IEnumerable<JsonElement> cards)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var card in cards) Upsert(conn, card, tx);
        tx.Commit();
    }

    private static void Upsert(SqliteConnection conn, JsonElement card, SqliteTransaction? tx)
    {
        var set = card.TryGetProperty("set", out var s) ? s : default;
        var images = card.TryGetProperty("images", out var i) ? i : default;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO cards (id, name, set_id, set_name, set_series, number, rarity, supertype,
                               subtypes, types, hp, artist, release_date, image_small, image_large,
                               payload, cached_at)
            VALUES ($id, $name, $setId, $setName, $setSeries, $number, $rarity, $supertype,
                    $subtypes, $types, $hp, $artist, $releaseDate, $imageSmall, $imageLarge,
                    $payload, $cachedAt)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, set_id=excluded.set_id, set_name=excluded.set_name,
                set_series=excluded.set_series, number=excluded.number, rarity=excluded.rarity,
                supertype=excluded.supertype, subtypes=excluded.subtypes, types=excluded.types,
                hp=excluded.hp, artist=excluded.artist, release_date=excluded.release_date,
                image_small=excluded.image_small, image_large=excluded.image_large,
                payload=excluded.payload, cached_at=excluded.cached_at;
            """;

        cmd.Parameters.AddWithValue("$id", Str(card, "id") ?? "");
        cmd.Parameters.AddWithValue("$name", Str(card, "name") ?? "");
        cmd.Parameters.AddWithValue("$setId", (object?)Str(set, "id") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$setName", (object?)Str(set, "name") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$setSeries", (object?)Str(set, "series") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$number", (object?)Str(card, "number") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rarity", (object?)Str(card, "rarity") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$supertype", (object?)Str(card, "supertype") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subtypes", (object?)Arr(card, "subtypes") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$types", (object?)Arr(card, "types") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hp", (object?)Str(card, "hp") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)Str(card, "artist") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$releaseDate", (object?)Str(set, "releaseDate") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imageSmall", (object?)Str(images, "small") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imageLarge", (object?)Str(images, "large") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", card.GetRawText());
        cmd.Parameters.AddWithValue("$cachedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the cached payload for a card, or null if we've never seen it.</summary>
    public string? GetPayload(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM cards WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public bool Has(string id) => GetPayload(id) is not null;

    /// <summary>
    /// Looks for already-cached cards matching any combination of set, number and
    /// name. CSV imports lean on this hard: re-importing a file, or a file with many
    /// cards from one set, resolves entirely offline instead of re-querying the API.
    /// </summary>
    public List<string> FindPayloads(string? setId, string? number, string? name, int limit = 12)
    {
        var clauses = new List<string>();
        var pars = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(setId))
        {
            clauses.Add("LOWER(set_id) = $setId");
            pars["$setId"] = setId.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(number))
        {
            clauses.Add("LOWER(number) = $number");
            pars["$number"] = number.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            clauses.Add("LOWER(name) LIKE $name");
            pars["$name"] = $"%{name.Trim().ToLowerInvariant()}%";
        }

        if (clauses.Count == 0) return [];

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT payload FROM cards WHERE {string.Join(" AND ", clauses)} LIMIT {limit}";
        foreach (var (k, v) in pars) cmd.Parameters.AddWithValue(k, v);

        var results = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(r.GetString(0));
        return results;
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    private static string? Arr(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Array
            ? v.GetRawText()
            : null;
}
