using System.Reflection;
using Microsoft.Data.Sqlite;
using CardVault.Data;

namespace CardVault.Services;

public sealed record BackupInfo(string Name, long SizeBytes, DateTime CreatedUtc, string Reason);

/// <summary>
/// Keeps timestamped copies of the collection database.
///
/// Backups use SQLite's own backup API rather than copying the file: with
/// write-ahead logging on, the .db file alone can be an incomplete picture of the
/// database, so a plain file copy of a live database can produce something that
/// won't open.
/// </summary>
public sealed class BackupService(DataPaths paths, Db db, ILogger<BackupService> log)
{
    private const int KeepCount = 10;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);

    private static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Runs at startup. Always takes a backup when the app version has changed
    /// since last run — that's the update case, and the one most likely to lose
    /// data — and otherwise at most once a day.
    /// </summary>
    public void RunStartupBackup()
    {
        try
        {
            if (!File.Exists(paths.DatabaseFile)) return;

            var lastVersion = GetSetting("app_version");
            var versionChanged = lastVersion is not null && lastVersion != AppVersion;

            var latest = List().FirstOrDefault();
            var dueByTime = latest is null || DateTime.UtcNow - latest.CreatedUtc >= MinimumInterval;

            if (versionChanged)
            {
                Create($"update-{lastVersion}-to-{AppVersion}");
                log.LogInformation("Version changed from {Old} to {New}; backed up before continuing",
                    lastVersion, AppVersion);
            }
            else if (dueByTime)
            {
                Create("daily");
            }

            SetSetting("app_version", AppVersion);
            Prune();
        }
        catch (Exception e)
        {
            // A failed backup must never stop the app from starting.
            log.LogError(e, "Startup backup failed");
        }
    }

    public BackupInfo Create(string reason)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);

        var safeReason = string.Concat(reason.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var name = $"vault-{stamp}-{safeReason}.db";
        var destination = Path.Combine(paths.BackupsDirectory, name);

        using (var source = db.Open())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }

        var info = new FileInfo(destination);
        log.LogInformation("Backed up collection to {Name} ({Size:N0} bytes)", name, info.Length);
        return new BackupInfo(name, info.Length, DateTime.UtcNow, reason);
    }

    public List<BackupInfo> List()
    {
        if (!Directory.Exists(paths.BackupsDirectory)) return [];

        return Directory.EnumerateFiles(paths.BackupsDirectory, "vault-*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(
                Name: f.Name,
                SizeBytes: f.Length,
                CreatedUtc: f.CreationTimeUtc,
                Reason: ParseReason(f.Name)))
            .ToList();
    }

    /// <summary>Full path of a backup, or null if the name doesn't resolve to one.</summary>
    public string? ResolvePath(string name)
    {
        // Guard against path traversal in a user-supplied name.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;

        var path = Path.Combine(paths.BackupsDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        var path = ResolvePath(name);
        if (path is null) return false;
        File.Delete(path);
        return true;
    }

    private void Prune()
    {
        var all = List();
        foreach (var old in all.Skip(KeepCount))
        {
            try
            {
                File.Delete(Path.Combine(paths.BackupsDirectory, old.Name));
                log.LogInformation("Pruned old backup {Name}", old.Name);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not prune backup {Name}", old.Name);
            }
        }
    }

    private static string ParseReason(string fileName)
    {
        // vault-20260813-004512-daily.db  ->  daily
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('-');
        return parts.Length >= 4 ? string.Join("-", parts.Skip(3)) : "manual";
    }

    private string? GetSetting(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetSetting(string key, string value)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
