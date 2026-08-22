namespace CardVault.Data;

/// <summary>
/// Decides where your collection lives — and deliberately keeps it out of the
/// application folder.
///
/// Data used to sit next to the binary, which meant replacing the app folder to
/// install an update destroyed the collection with it. It now lives in the normal
/// per-user data location for the platform, so the app can be replaced, moved or
/// reinstalled without touching your cards.
/// </summary>
public sealed class DataPaths
{
    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "vault.db");
    public string ImagesDirectory => Path.Combine(Root, "images");
    public string BackupsDirectory => Path.Combine(Root, "backups");

    /// <summary>
    /// Artwork for the offline catalogue, kept apart from the on-demand image cache
    /// so that deleting the catalogue is a matter of removing one folder and can't
    /// take the images for cards you own with it. Created only if the catalogue is
    /// actually downloaded — an unused feature shouldn't leave a folder behind.
    /// </summary>
    public string CatalogueImagesDirectory => Path.Combine(Root, "catalogue-images");

    /// <summary>
    /// Your own photographs of the cards you own, as opposed to catalogue artwork.
    ///
    /// Its own folder, created only when the feature is switched on, so an unused
    /// feature leaves nothing behind and turning it off is one directory to remove.
    /// Not covered by the automatic backup, which copies the database alone --
    /// scans are large and would turn a quick safety copy into a slow one.
    /// </summary>
    public string PhotosDirectory => Path.Combine(Root, "photos");

    /// <summary>Where data lived before it was moved out of the app folder.</summary>
    public static string LegacyDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public bool MigratedFromLegacy { get; private set; }
    public string? MigratedFrom { get; private set; }

    public DataPaths(IConfiguration config, ILogger<DataPaths> log)
    {
        Root = Resolve(config);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(BackupsDirectory);

        MigrateLegacy(log);

        log.LogInformation("Collection data directory: {Root}", Root);
    }

    /// <summary>
    /// Places an existing collection might be, in the order we'd trust them.
    ///
    /// The per-user path matters once the app runs as a service: a service account
    /// has its own LOCALAPPDATA, so it would resolve to a different folder and come
    /// up with an empty collection while yours sat untouched next door. Installing
    /// pins an explicit directory, and this brings the existing data across to it.
    /// </summary>
    private static IEnumerable<string> LegacyCandidates()
    {
        yield return LegacyDirectory;
        yield return DefaultUserDirectory();

        // The app was called Pokémon Vault before, and its data folder was named
        // to match. Without this an existing collection would simply be orphaned
        // by the rename — the app would start empty and look like it had lost
        // everything, with the real database sitting in a folder nothing reads.
        yield return UserDirectoryNamed("PokemonVault");
    }

    private static string DefaultUserDirectory() => UserDirectoryNamed("CardVault");

    private static string UserDirectoryNamed(string folder)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
        return Path.Combine(baseDir, folder);
    }

    private static string Resolve(IConfiguration config)
    {
        var configured = config["CardVault:DataDirectory"] ?? config["CARDVAULT_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());

        // LocalApplicationData maps to %LOCALAPPDATA% on Windows and
        // ~/.local/share on Linux and macOS.
        return DefaultUserDirectory();
    }

    /// <summary>
    /// Moves a collection created by an older build into the new location. Copies
    /// rather than moves, so the original stays put as a safety net if anything
    /// goes wrong.
    /// </summary>
    private void MigrateLegacy(ILogger log)
    {
        try
        {
            // Never overwrite a collection that's already here.
            if (File.Exists(DatabaseFile)) return;

            // Pick the most recently written database, not the first candidate
            // that happens to exist. Several of these can be present at once —
            // an abandoned in-app folder from an old build, a previous name's
            // folder — and taking them in a fixed order silently restored a
            // months-stale collection over the live one.
            var legacy = LegacyCandidates()
                .Where(candidate =>
                    File.Exists(Path.Combine(candidate, "vault.db"))
                    && Path.GetFullPath(candidate) != Path.GetFullPath(Root))
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(Path.Combine(candidate, "vault.db")))
                .FirstOrDefault();

            if (legacy is null) return;

            var legacyDb = Path.Combine(legacy, "vault.db");
            log.LogInformation("Found a collection at {Legacy}; copying it to {Root}", legacy, Root);

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = legacyDb + suffix;
                if (File.Exists(src)) File.Copy(src, DatabaseFile + suffix, overwrite: false);
            }

            var legacyImages = Path.Combine(legacy, "images");
            if (Directory.Exists(legacyImages))
            {
                foreach (var file in Directory.EnumerateFiles(legacyImages))
                {
                    var dest = Path.Combine(ImagesDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Copy(file, dest);
                }
            }

            MigratedFromLegacy = true;
            MigratedFrom = legacy;
            log.LogInformation(
                "Collection migrated. The old copy at {Legacy} was left in place and can be deleted once you're happy.",
                legacy);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not migrate the existing collection — starting with an empty one. "
                            + "Your old data is untouched.");
        }
    }
}
