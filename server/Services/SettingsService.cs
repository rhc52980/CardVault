using PokemonVault.Data;

namespace PokemonVault.Services;

public sealed record ApiKeyStatus(bool Configured, string? Masked, string Source);

/// <summary>
/// App settings, and in particular the pokemontcg.io API key.
///
/// The key is resolved fresh on each request rather than baked in at startup, so
/// entering one in the UI takes effect immediately without a restart. A key saved
/// through the UI wins over the config file and environment, so the box on screen
/// is always the thing in control.
/// </summary>
public sealed class SettingsService(Db db, IConfiguration config)
{
    private const string ApiKeySetting = "pokemontcg_api_key";

    /// <summary>The key to use right now, or null if none is configured anywhere.</summary>
    public string? GetApiKey()
    {
        var stored = Get(ApiKeySetting);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        var fromConfig = config["PokemonTcg:ApiKey"] ?? config["POKEMONTCG_API_KEY"];
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
    }

    public ApiKeyStatus GetApiKeyStatus()
    {
        var stored = Get(ApiKeySetting);
        if (!string.IsNullOrWhiteSpace(stored))
            return new ApiKeyStatus(true, Mask(stored), "Saved in this app");

        var fromConfig = config["PokemonTcg:ApiKey"] ?? config["POKEMONTCG_API_KEY"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return new ApiKeyStatus(true, Mask(fromConfig), "appsettings.Local.json or environment");

        return new ApiKeyStatus(false, null, "Not set");
    }

    public void SetApiKey(string key) => Set(ApiKeySetting, key.Trim());

    /// <summary>Removes the saved key, falling back to config or environment if present.</summary>
    public void ClearApiKey() => Delete(ApiKeySetting);

    /// <summary>
    /// Only ever show the ends. The server has no authentication, so anyone who can
    /// reach it could otherwise read the key straight back out.
    /// </summary>
    private static string Mask(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length <= 8) return new string('•', trimmed.Length);
        return $"{trimmed[..4]}{new string('•', Math.Min(trimmed.Length - 8, 24))}{trimmed[^4..]}";
    }

    public string? Get(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
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

    public void Delete(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
