using System.Text.Json;
using CardVault.Models;

namespace CardVault.Data;

/// <summary>
/// The collections this installation holds.
///
/// For a household rather than for strangers: one password gets you in, and then you
/// pick whose cards you're looking at. Nobody is hidden from anybody — that would be a
/// different feature with a much larger and more dangerous surface, and two separate
/// installs give stronger isolation than any amount of code here could.
///
/// Kept in a small JSON file beside the data rather than in a database, because the
/// list has to be readable before we know which database to open.
/// </summary>
public sealed class VaultRegistry(DataPaths paths, ILogger<VaultRegistry> log)
{
    private readonly Lock _gate = new();

    private string RegistryFile => Path.Combine(paths.Root, "vaults.json");

    private sealed record Entry(string Id, string Name, string CreatedAt);

    /// <summary>
    /// Every vault, the default one first.
    ///
    /// The default is not in the file and never has been. It's the collection that
    /// existed before this feature did, its files sit at the root, and describing it
    /// in a registry it predates would mean an upgrade that rewrites data on disk to
    /// say what was already true.
    /// </summary>
    public List<VaultInfo> List()
    {
        var vaults = new List<VaultInfo>
        {
            new(CurrentVault.Default, DefaultName(), true),
        };

        foreach (var e in Read())
            vaults.Add(new VaultInfo(e.Id, e.Name, false));

        return vaults;
    }

    public bool Exists(string id)
        => id == CurrentVault.Default || Read().Any(e => e.Id == id);

    public (bool Ok, string? Error, string? Id) Create(string? name)
    {
        var display = (name ?? "").Trim();
        if (display.Length == 0) return (false, "Give the collection a name.", null);
        if (display.Length > 60) display = display[..60].TrimEnd();

        var id = Slug(display);
        if (id.Length == 0) return (false, "That name has no letters or numbers in it.", null);
        if (id == CurrentVault.Default) return (false, "That name is reserved.", null);

        lock (_gate)
        {
            var entries = Read();
            if (entries.Any(e => e.Id == id)) return (false, "There's already a collection with that name.", null);

            entries.Add(new Entry(id, display, DateTime.UtcNow.ToString("o")));
            Write(entries);
        }

        // The database and its folder appear the first time something opens it, which
        // keeps creation here to a single line in a file.
        return (true, null, id);
    }

    public bool Rename(string id, string? name)
    {
        var display = (name ?? "").Trim();
        if (display.Length == 0) return false;
        if (display.Length > 60) display = display[..60].TrimEnd();

        if (id == CurrentVault.Default)
        {
            // The default has no registry row to rename, so its name is a setting.
            // Stored under the root rather than inside its database for symmetry with
            // the others, which have to be nameable before their database is opened.
            File.WriteAllText(DefaultNameFile, display);
            return true;
        }

        lock (_gate)
        {
            var entries = Read();
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return false;

            entries[index] = entries[index] with { Name = display };
            Write(entries);
        }

        return true;
    }

    /// <summary>
    /// Removes a collection and everything in it.
    ///
    /// Genuinely deletes cards, sales, photos and backups, which is why the default
    /// vault can't be removed at all: it's the one an install starts with, and a
    /// misplaced click there would take the collection the app was bought for.
    /// </summary>
    public (bool Ok, string? Error) Delete(string id)
    {
        if (id == CurrentVault.Default) return (false, "The first collection can't be removed.");

        lock (_gate)
        {
            var entries = Read();
            if (entries.RemoveAll(e => e.Id == id) == 0) return (false, "No such collection.");
            Write(entries);
        }

        var dir = Path.Combine(paths.Root, "vaults", id);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            // The registry entry is already gone, so the collection has disappeared
            // from the app either way. Say so rather than pretending it all went.
            log.LogWarning(e, "Removed vault {Vault} from the registry but could not delete {Dir}", id, dir);
            return (true, "Removed, but its files could not be deleted. The log has the path.");
        }

        return (true, null);
    }

    // ------------------------------------------------------------------- plumbing

    private string DefaultNameFile => Path.Combine(paths.Root, "vault-name.txt");

    private string DefaultName()
    {
        try
        {
            if (File.Exists(DefaultNameFile))
            {
                var name = File.ReadAllText(DefaultNameFile).Trim();
                if (name.Length > 0) return name;
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the default collection's name");
        }

        return "My collection";
    }

    /// <summary>
    /// A name turned into something safe to use as a folder. Lowercase, letters,
    /// digits and dashes only — this ends up as a path, so anything else is a way of
    /// writing files where they weren't meant to go.
    /// </summary>
    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    private List<Entry> Read()
    {
        try
        {
            if (!File.Exists(RegistryFile)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(RegistryFile)) ?? [];
        }
        catch (Exception e)
        {
            // Losing the list would strand collections that are still on disk, so this
            // says so loudly rather than quietly starting again with none.
            log.LogError(e, "Could not read {File} — additional collections will not be listed", RegistryFile);
            return [];
        }
    }

    private void Write(List<Entry> entries)
        => File.WriteAllText(RegistryFile, JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
}
