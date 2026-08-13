using System.Net;
using System.Text.Json;

namespace PokemonVault.Services;

/// <summary>
/// Thin wrapper over the pokemontcg.io v2 API. Callers get raw JsonElements back —
/// we store the full payload rather than modelling every field, because the card
/// schema varies a lot between Pokémon, Trainer and Energy cards.
/// </summary>
public sealed class PokemonTcgClient(HttpClient http, ILogger<PokemonTcgClient> log)
{
    private const string Base = "https://api.pokemontcg.io/v2";

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
            using var res = await http.GetAsync(url, ct);

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
