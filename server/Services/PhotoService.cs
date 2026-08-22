using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Your own photographs of the cards you own.
///
/// Distinct from catalogue artwork, which shows what the card looks like in general.
/// This shows what *your* copy looks like: the corner wear you're claiming, the
/// centring, the slab label, which of two copies is which. That makes it a property
/// of the collection entry rather than of the card.
///
/// Off until you turn it on, and switching it off takes the folder with it. An
/// unused feature should leave nothing behind, and someone who never wanted this
/// shouldn't be carrying a directory around because they clicked once.
/// </summary>
public sealed class PhotoService(Db db, DataPaths paths, SettingsService settings, ILogger<PhotoService> log)
{
    private const string EnabledSetting = "photos_enabled";

    /// <summary>
    /// What we'll store. Deliberately short: these are read back by a browser, and
    /// accepting anything a browser won't render is a way of losing someone's file.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    /// <summary>
    /// Generous, because a flatbed scan of a card at any useful resolution is a few
    /// megabytes and silently rejecting one would look like the feature is broken.
    /// </summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    public bool Enabled => settings.Get(EnabledSetting) == "1";

    /// <summary>
    /// Turning it off leaves the photos alone. Losing files because a switch was
    /// flipped would be indefensible -- <see cref="DeleteAll"/> is the one that
    /// removes them, and it says so.
    /// </summary>
    public void SetEnabled(bool enabled) => settings.Set(EnabledSetting, enabled ? "1" : "0");

    public PhotoStatus Status()
    {
        var (count, bytes) = Usage();
        return new PhotoStatus(Enabled, count, bytes);
    }

    /// <summary>
    /// Stores a photo against one entry, replacing whatever was there.
    ///
    /// Named by entry id plus the original extension. Ids come from AUTOINCREMENT and
    /// are never reused, so a file can't end up describing a card that isn't the one
    /// it was taken of.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AttachAsync(
        long entryId, Stream content, string? fileName, CancellationToken ct)
    {
        if (!Enabled) return (false, "Card photos are switched off. Turn them on in Settings first.");

        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!AllowedTypes.ContainsKey(extension))
            return (false, "That needs to be a PNG, JPEG or WebP.");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) return (false, "That file was empty.");
        if (buffer.Length > MaxBytes) return (false, $"That's larger than the {MaxBytes / 1024 / 1024} MB limit.");

        try
        {
            Directory.CreateDirectory(paths.PhotosDirectory);

            // The old one goes first: a replacement with a different extension would
            // otherwise leave the previous file orphaned in the folder for good.
            RemoveFile(entryId);

            var name = $"{entryId}{extension}";
            await File.WriteAllBytesAsync(Path.Combine(paths.PhotosDirectory, name), buffer.ToArray(), ct);
            SetPhotoColumn(entryId, name);
            return (true, null);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not store the photo for entry {EntryId}", entryId);
            return (false, "That photo could not be saved. The log has the details.");
        }
    }

    public bool Detach(long entryId)
    {
        if (PhotoName(entryId) is null) return false;
        RemoveFile(entryId);
        SetPhotoColumn(entryId, null);
        return true;
    }

    /// <summary>The file to serve for an entry, or null if it has none on disk.</summary>
    public (string Path, string ContentType)? Resolve(long entryId)
    {
        if (PhotoName(entryId) is not { } name) return null;

        var path = Path.Combine(paths.PhotosDirectory, name);
        if (!File.Exists(path)) return null;

        var type = AllowedTypes.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
        return (path, type);
    }

    /// <summary>
    /// Removes every photo and the folder with them. Separate from the switch on
    /// purpose: turning the feature off is reversible and this is not.
    /// </summary>
    public int DeleteAll()
    {
        var (count, _) = Usage();

        try
        {
            if (Directory.Exists(paths.PhotosDirectory))
                Directory.Delete(paths.PhotosDirectory, recursive: true);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not remove the photos directory");
        }

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collection SET photo = NULL WHERE photo IS NOT NULL";
        cmd.ExecuteNonQuery();

        return count;
    }

    /// <summary>
    /// Files left behind by entries that have since been sold or removed.
    ///
    /// Deleting a card doesn't route through here -- there are several ways for an
    /// entry to disappear, including a bulk removal and an import being undone, and
    /// a sweep is more reliable than remembering to call something from each of them.
    /// </summary>
    public int CleanUpOrphans()
    {
        if (!Directory.Exists(paths.PhotosDirectory)) return 0;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT photo FROM collection WHERE photo IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) live.Add(r.GetString(0));
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(paths.PhotosDirectory))
        {
            if (live.Contains(Path.GetFileName(file))) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not remove orphaned photo {File}", file);
            }
        }

        return removed;
    }

    private (int Count, long Bytes) Usage()
    {
        if (!Directory.Exists(paths.PhotosDirectory)) return (0, 0);

        try
        {
            var files = Directory.EnumerateFiles(paths.PhotosDirectory).Select(f => new FileInfo(f)).ToList();
            return (files.Count, files.Sum(f => f.Length));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not measure the photos directory");
            return (0, 0);
        }
    }

    private string? PhotoName(long entryId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT photo FROM collection WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        return cmd.ExecuteScalar() as string;
    }

    private void RemoveFile(long entryId)
    {
        if (PhotoName(entryId) is not { } name) return;

        try
        {
            var path = Path.Combine(paths.PhotosDirectory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not remove the old photo for entry {EntryId}", entryId);
        }
    }

    private void SetPhotoColumn(long entryId, string? name)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collection SET photo = $photo WHERE id = $id";
        cmd.Parameters.AddWithValue("$photo", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }
}
