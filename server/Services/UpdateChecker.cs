using System.Net.Http.Headers;
using System.Text.Json;

namespace PokemonVault.Services;

/// <summary>
/// Optional, opt-in check for a newer release on GitHub. Off by default —
/// "makes no outbound calls you didn't ask for" is worth keeping true, and the
/// app already talks to pokemontcg.io only because you asked it to.
///
/// It only ever reads. Nothing is downloaded and nothing is installed; the app
/// shows a badge linking to the release page and you run the updater yourself.
/// </summary>
public sealed class UpdateChecker(
    IHttpClientFactory http,
    SettingsService settings,
    IConfiguration config,
    ILogger<UpdateChecker> log,
    string currentVersion)
{
    private const string EnabledSetting = "update_check";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Enabled =>
        settings.Get(EnabledSetting) is { } s
            ? s == "true"
            : config.GetValue("PokemonVault:UpdateCheck", false);

    public string CurrentVersion => currentVersion;
    public string? LatestVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public DateTime? LastCheckedUtc { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public void SetEnabled(bool enabled)
    {
        settings.Set(EnabledSetting, enabled ? "true" : "false");
        if (!enabled)
        {
            // Clear the badge immediately rather than leaving a stale one behind.
            LatestVersion = null;
            ReleaseUrl = null;
            UpdateAvailable = false;
        }
    }

    /// <summary>
    /// Checks at most once a day. Every failure — no internet, rate limit,
    /// private repo, malformed response — is swallowed: an update check must
    /// never stop you looking at your collection.
    /// </summary>
    public async Task MaybeCheckAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        if (LastCheckedUtc is { } last && DateTime.UtcNow - last < Interval) return;
        if (!await _gate.WaitAsync(0, ct)) return;

        try
        {
            LastCheckedUtc = DateTime.UtcNow;
            var repo = config["PokemonVault:UpdateRepo"] ?? "rhc52980/Pokemon_Vault";

            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            // GitHub rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PokemonVault", Sanitise(currentVersion)));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var res = await client.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (!res.IsSuccessStatusCode)
            {
                // 404 is the normal answer for a private repo, or one with no
                // releases yet — not worth shouting about.
                log.LogDebug("Update check returned {Status}", (int)res.StatusCode);
                return;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return;

            LatestVersion = tag.TrimStart('v', 'V');
            ReleaseUrl = url;
            UpdateAvailable = IsNewer(LatestVersion, currentVersion);

            if (UpdateAvailable)
                log.LogInformation("Update available: {Latest} (running {Current})", LatestVersion, currentVersion);
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Update check failed");
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!Version.TryParse(Normalise(candidate), out var a)) return false;
        if (!Version.TryParse(Normalise(current), out var b)) return false;
        return a > b;
    }

    /// <summary>"v1.3" and "1.3.0+abc123" both have to become something Version can parse.</summary>
    private static string Normalise(string? v)
    {
        var s = (v ?? "").Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        var parts = s.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => s,
        };
    }

    /// <summary>A User-Agent product version can't contain the '+' of a source stamp.</summary>
    private static string Sanitise(string version) => version.Split('+')[0];
}
