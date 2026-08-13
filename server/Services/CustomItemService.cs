using System.Text.Json;
using PokemonVault.Data;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>
/// Items the catalogue doesn't carry — sealed booster boxes, elite trainer boxes,
/// Japanese promos, error cards.
///
/// These are stored as synthetic rows in the same `cards` table rather than a
/// parallel structure, so the collection grid, filtering, stats and detail view all
/// treat them like anything else. The `is_custom` flag keeps them out of the places
/// where they'd be wrong: price refreshes and set completion.
/// </summary>
public sealed class CustomItemService(
    Db db, DataPaths paths, CollectionService collection, HttpClient http, ILogger<CustomItemService> log)
{
    public const string IdPrefix = "custom-";

    private readonly string _imageDir = paths.ImagesDirectory;

    public static bool IsCustomId(string cardId) => cardId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Creates the synthetic card and the collection entry in one go, since a custom
    /// item only exists because you own one.
    /// </summary>
    public async Task<(string CardId, long EntryId)> CreateAsync(
        CustomItemRequest req, Stream? imageStream, CancellationToken ct)
    {
        var cardId = IdPrefix + Guid.NewGuid().ToString("n")[..12];
        var category = string.IsNullOrWhiteSpace(req.Category) ? "Custom" : req.Category.Trim();

        string? imagePath = null;
        if (imageStream is not null) imagePath = await SaveUploadAsync(cardId, imageStream, ct);
        else if (!string.IsNullOrWhiteSpace(req.ImageUrl)) imagePath = await SaveFromUrlAsync(cardId, req.ImageUrl, ct);

        // Enough of a card shape that the detail view and pricing helpers don't choke.
        var payload = JsonSerializer.Serialize(new
        {
            id = cardId,
            name = req.Name,
            supertype = category,
            images = new { small = imagePath, large = imagePath },
        });

        InsertCard(cardId, req.Name, category, imagePath, payload);

        var entryId = collection.Add(new AddEntryRequest(
            CardId: cardId,
            Quantity: Math.Max(1, req.Quantity),
            Variant: "custom",
            Condition: string.IsNullOrWhiteSpace(req.Condition) ? "NM" : req.Condition,
            Grade: req.Grade,
            PurchasePrice: req.PurchasePrice,
            PurchaseDate: req.PurchaseDate,
            Notes: req.Notes,
            ManualValue: req.Value));

        return (cardId, entryId);
    }

    /// <summary>
    /// Deleting the last entry for a custom item removes the synthetic card too —
    /// nothing else will ever reference it, and leaving it behind would clutter the
    /// cache with phantom cards.
    /// </summary>
    public void CleanUpOrphans()
    {
        using var conn = db.Open();

        // Collect ids first so the uploaded artwork can go with them.
        var orphans = new List<string>();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT id FROM cards
                WHERE is_custom = 1 AND id NOT IN (SELECT card_id FROM collection)
                """;
            using var r = find.ExecuteReader();
            while (r.Read()) orphans.Add(r.GetString(0));
        }

        if (orphans.Count == 0) return;

        using (var del = conn.CreateCommand())
        {
            del.CommandText = """
                DELETE FROM cards
                WHERE is_custom = 1 AND id NOT IN (SELECT card_id FROM collection)
                """;
            del.ExecuteNonQuery();
        }

        foreach (var id in orphans)
        foreach (var size in new[] { "small", "large" })
        {
            try
            {
                var file = Path.Combine(_imageDir, $"{id}_{size}.png");
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not delete image for orphaned item {CardId}", id);
            }
        }

        log.LogInformation("Removed {Count} orphaned custom item(s)", orphans.Count);
    }

    private void InsertCard(string cardId, string name, string category, string? image, string payload)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, set_id, set_name, set_series, number, rarity,
                               supertype, subtypes, types, hp, artist, release_date,
                               image_small, image_large, payload, cached_at, is_custom)
            VALUES ($id, $name, NULL, $category, NULL, NULL, NULL,
                    $category, NULL, NULL, NULL, NULL, NULL,
                    $image, $image, $payload, $cachedAt, 1)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        // set_id stays NULL deliberately so custom items never skew set completion.
        cmd.Parameters.AddWithValue("$category", category);
        cmd.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$cachedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes the image where ImageCache already looks, so /img/{cardId}/{size}
    /// serves custom items with no special-casing.
    /// </summary>
    private async Task<string?> SaveUploadAsync(string cardId, Stream stream, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_imageDir);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0) return null;

            foreach (var size in new[] { "small", "large" })
                await File.WriteAllBytesAsync(Path.Combine(_imageDir, $"{cardId}_{size}.png"), bytes, ct);

            return $"/img/{cardId}/small";
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not store uploaded image for {CardId}", cardId);
            return null;
        }
    }

    private async Task<string?> SaveFromUrlAsync(string cardId, string url, CancellationToken ct)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url, ct);
            using var stream = new MemoryStream(bytes);
            return await SaveUploadAsync(cardId, stream, ct);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not fetch image {Url} for {CardId}", url, cardId);
            return null;
        }
    }
}
