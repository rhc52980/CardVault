namespace PokemonVault.Data;

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
    }

    private static string DefaultUserDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
        return Path.Combine(baseDir, "PokemonVault");
    }

    private static string Resolve(IConfiguration config)
    {
        var configured = config["PokemonVault:DataDirectory"] ?? config["POKEMONVAULT_DATA_DIR"];
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

            var legacy = LegacyCandidates().FirstOrDefault(candidate =>
                File.Exists(Path.Combine(candidate, "vault.db"))
                && Path.GetFullPath(candidate) != Path.GetFullPath(Root));

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
