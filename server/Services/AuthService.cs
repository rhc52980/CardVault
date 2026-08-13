using System.Collections.Concurrent;
using System.Security.Cryptography;
using PokemonVault.Data;

namespace PokemonVault.Services;

public sealed record SessionInfo(string TokenPrefix, string CreatedAt, string LastSeen, string? UserAgent, string? CreatedIp, bool IsCurrent);

/// <summary>
/// Password protection for the vault.
///
/// Deliberately opt-in: with no password set the app behaves exactly as it did
/// before, which keeps a trusted home network friction-free. Setting a password
/// turns on enforcement for the API and card images — the pages themselves stay
/// public because the login screen has to load somehow.
///
/// Sessions live in the database rather than in a self-contained signed cookie, so
/// signing out genuinely revokes access rather than just discarding the client's
/// copy. That matters when the reason you're signing out is a lost phone.
/// </summary>
public sealed class AuthService(Db db, SettingsService settings, ILogger<AuthService> log)
{
    public const string CookieName = "pv_session";

    private const string HashSetting = "auth_password_hash";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // OWASP's floor for PBKDF2-HMAC-SHA256 at time of writing. Stored alongside each
    // hash so it can be raised later without invalidating existing passwords.
    private const int DefaultIterations = 600_000;

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Failed attempts per client address. In memory on purpose: a restart clearing
    /// it is fine, and it avoids a write to disk on every guess (which would itself
    /// be a denial-of-service vector).
    /// </summary>
    private readonly ConcurrentDictionary<string, (int Count, DateTime LockedUntil)> _failures = new();

    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutBase = TimeSpan.FromSeconds(30);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(settings.Get(HashSetting));

    // ------------------------------------------------------------- passwords

    /// <summary>Sets the first password, enabling protection.</summary>
    public (bool Ok, string? Error) SetInitialPassword(string password)
    {
        if (IsEnabled) return (false, "A password is already set. Change it instead.");
        var problem = Validate(password);
        if (problem is not null) return (false, problem);

        settings.Set(HashSetting, Hash(password));
        log.LogInformation("Vault password set — authentication is now required");
        return (true, null);
    }

    public (bool Ok, string? Error) ChangePassword(string current, string next)
    {
        if (!IsEnabled) return (false, "No password is set yet.");
        if (!VerifyPassword(current)) return (false, "That current password isn't right.");

        var problem = Validate(next);
        if (problem is not null) return (false, problem);

        settings.Set(HashSetting, Hash(next));

        // Anything signed in with the old password loses access — that's the point
        // of changing it.
        RevokeAllSessions();
        log.LogInformation("Vault password changed; all sessions revoked");
        return (true, null);
    }

    /// <summary>Turns protection off again, which only makes sense on a trusted network.</summary>
    public (bool Ok, string? Error) Disable(string currentPassword)
    {
        if (!IsEnabled) return (true, null);
        if (!VerifyPassword(currentPassword)) return (false, "That password isn't right.");

        settings.Delete(HashSetting);
        RevokeAllSessions();
        log.LogWarning("Vault password removed — the app is no longer protected");
        return (true, null);
    }

    private static string? Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "Enter a password.";
        if (password.Length < 10) return "Use at least 10 characters — this may end up reachable from the internet.";
        return null;
    }

    public bool VerifyPassword(string password)
    {
        var stored = settings.Get(HashSetting);
        if (string.IsNullOrWhiteSpace(stored)) return false;

        // Format: pbkdf2$<iterations>$<salt-b64>$<hash-b64>
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            // Constant-time: a length-dependent early exit would leak information.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // -------------------------------------------------------- rate limiting

    public TimeSpan? LockoutRemaining(string clientKey)
    {
        if (!_failures.TryGetValue(clientKey, out var state)) return null;
        var remaining = state.LockedUntil - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public void RecordFailure(string clientKey)
    {
        _failures.AddOrUpdate(
            clientKey,
            _ => (1, DateTime.MinValue),
            (_, prev) =>
            {
                var count = prev.Count + 1;
                if (count < MaxAttempts) return (count, DateTime.MinValue);

                // Back off harder the longer it goes on, so guessing stops being viable.
                var over = count - MaxAttempts + 1;
                var wait = TimeSpan.FromSeconds(LockoutBase.TotalSeconds * Math.Pow(2, Math.Min(over, 6)));
                return (count, DateTime.UtcNow.Add(wait));
            });
    }

    public void ClearFailures(string clientKey) => _failures.TryRemove(clientKey, out _);

    // -------------------------------------------------------------- sessions

    public string CreateSession(string? userAgent, string? ip)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (token, created_at, expires_at, last_seen, user_agent, created_ip)
            VALUES ($token, $created, $expires, $seen, $ua, $ip)
            """;
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$created", now.ToString("o"));
        cmd.Parameters.AddWithValue("$expires", now.Add(SessionLifetime).ToString("o"));
        cmd.Parameters.AddWithValue("$seen", now.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", (object?)Trim(userAgent, 200) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        PruneExpired();
        return token;
    }

    /// <summary>Checks a token and refreshes its last-seen time.</summary>
    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT expires_at FROM sessions WHERE token = $token";
        cmd.Parameters.AddWithValue("$token", token);

        if (cmd.ExecuteScalar() is not string expiresRaw) return false;
        if (!DateTime.TryParse(expiresRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires)
            || expires <= DateTime.UtcNow)
        {
            RevokeSession(token);
            return false;
        }

        using var touch = conn.CreateCommand();
        touch.CommandText = "UPDATE sessions SET last_seen = $seen WHERE token = $token";
        touch.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("o"));
        touch.Parameters.AddWithValue("$token", token);
        touch.ExecuteNonQuery();

        return true;
    }

    public void RevokeSession(string token)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE token = $token";
        cmd.Parameters.AddWithValue("$token", token);
        cmd.ExecuteNonQuery();
    }

    public void RevokeAllSessions()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Signed-in devices, so you can spot one you don't recognise.</summary>
    public List<SessionInfo> ListSessions(string? currentToken)
    {
        PruneExpired();

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT token, created_at, last_seen, user_agent, created_ip FROM sessions ORDER BY last_seen DESC";

        var sessions = new List<SessionInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var token = r.GetString(0);
            sessions.Add(new SessionInfo(
                // Never return whole tokens — one is enough to impersonate the session.
                TokenPrefix: token[..8],
                CreatedAt: r.GetString(1),
                LastSeen: r.GetString(2),
                UserAgent: r.IsDBNull(3) ? null : r.GetString(3),
                CreatedIp: r.IsDBNull(4) ? null : r.GetString(4),
                IsCurrent: token == currentToken));
        }
        return sessions;
    }

    private void PruneExpired()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE expires_at <= $now";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static string? Trim(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
