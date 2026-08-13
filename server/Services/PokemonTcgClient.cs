using System.Net;
using System.Text.Json;

namespace CardVault.Services;

/// <summary>
/// Thin wrapper over the pokemontcg.io v2 API. Callers get raw JsonElements back —
/// we store the full payload rather than modelling every field, because the card
/// schema varies a lot between Pokémon, Trainer and Energy cards.
/// </summary>
public sealed class PokemonTcgClient(HttpClient http, SettingsService settings, ILogger<PokemonTcgClient> log)
{
    private const string Base = "https://api.pokemontcg.io/v2";

    /// <summary>Whether a key is available at all — used to warn in the UI.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(settings.GetApiKey());

    /// <summary>
    /// Checks that pokemontcg.io responds when using the given key.
    ///
    /// This deliberately does NOT claim the key is valid, because the API gives us
    /// no way to tell: a correct key, a made-up key and no key at all all return
    /// 200 with identical headers, and there are no rate-limit headers to compare.
    /// An invalid key simply gets silently treated as unauthenticated. So all we can
    /// honestly report is whether the service answered.
    /// </summary>
    public async Task<bool> TestConnectionAsync(string key, CancellationToken ct)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, $"{Base}/sets?pageSize=1");
        probe.Headers.Add("X-Api-Key", key.Trim());

        using var res = await http.SendAsync(probe, ct);
        return res.IsSuccessStatusCode;
    }

    public async Task<JsonElement> SearchCardsAsync(string query, int page, int pageSize, CancellationToken ct)
    {
        // No orderBy: the API 500s on any sort over nested set.* fields, and the
        // default relevance order is more useful for search anyway.
        var url = $"{Base}/cards?q={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<JsonElement?> GetCardAsync(string id, CancellationToken ct)
    {
        try
        {
            var root = await GetJsonAsync($"{Base}/cards/{Uri.EscapeDataString(id)}", ct);
            return root.TryGetProperty("data", out var data) ? data.Clone() : null;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<JsonElement> GetSetsAsync(CancellationToken ct)
        => await GetJsonAsync($"{Base}/sets?pageSize=250&orderBy=-releaseDate", ct);

    /// <summary>
    /// GET with backoff. pokemontcg.io returns 429 under rate limiting and throws
    /// intermittent 500/502s even on valid queries, so both are worth retrying —
    /// a repeat of the identical request usually succeeds.
    /// </summary>
    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // Built per request so a key entered in the UI applies straight away.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var key = settings.GetApiKey();
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("X-Api-Key", key);

            using var res = await http.SendAsync(request, ct);

            if (IsTransient(res.StatusCode) && attempt < maxAttempts)
            {
                var wait = TimeSpan.FromSeconds(attempt * 1.5);
                log.LogWarning("pokemontcg.io returned {Status}, retry {Attempt}/{Max} in {Seconds}s",
                    (int)res.StatusCode, attempt, maxAttempts, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
    }

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
