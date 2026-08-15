using Microsoft.Data.Sqlite;
using CardVault.Data;

namespace CardVault.Services;

/// <summary>
/// Downloads card art once and serves it from disk afterwards. Card images never
/// change, so this makes the grid instant on repeat visits and keeps the collection
/// viewable with no internet connection.
/// </summary>
public sealed class ImageCache(
    Db db, DataPaths paths, CatalogueService catalogue, HttpClient http, ILogger<ImageCache> log)
{
    private readonly string _dir = paths.ImagesDirectory;

    // Guards against a burst of grid requests all downloading the same file at once.
    private readonly SemaphoreSlim _gate = new(8);

    public async Task<string?> GetLocalPathAsync(string cardId, string size, CancellationToken ct)
    {
        var wanted = size == "large" ? "large" : "small";
        var safeId = string.Concat(cardId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safeId.Length == 0) return null;

        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, $"{safeId}_{wanted}.png");
        if (File.Exists(file)) return file;

        var remote = RemoteUrl(cardId, wanted);
        if (remote is null) return null;

        await _gate.WaitAsync(ct);
        try
        {
            // Another request may have finished the download while we waited.
            if (File.Exists(file)) return file;

            var bytes = await http.GetByteArrayAsync(remote, ct);
            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not cache image for {CardId}", cardId);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? RemoteUrl(string cardId, string size)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT image_{size} FROM cards WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", cardId);
        if (cmd.ExecuteScalar() as string is { Length: > 0 } known) return known;

        // Nothing owned by that id. It may still be a card we know of from the offline
        // catalogue — an import row mid-review is exactly that, resolved but not yet
        // committed — and the review list is no use with the pictures missing.
        return catalogue.RemoteImageUrl(cardId, size);
    }
}
