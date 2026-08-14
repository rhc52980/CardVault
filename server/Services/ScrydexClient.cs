using System.Net;
using System.Text.Json;

namespace CardVault.Services;

/// <summary>A card as Scrydex describes it, reduced to what the vault stores.</summary>
public sealed record ScrydexCard(
    string Id,
    string Name,
    string? Number,
    string? Rarity,
    string? Supertype,
    string[] Subtypes,
    string[] Types,
    string? Hp,
    string? Artist,
    string? ExpansionId,
    string? ExpansionName,
    string? ReleaseDate,
    string? ImageSmall,
    string? ImageLarge,
    string Language,
    /// <summary>Best raw market price found, in the currency below. Null when unpriced.</summary>
    double? Market,
    double? Low,
    string Currency);

/// <summary>
/// Scrydex, used for the cards pokemontcg.io doesn't have — Japanese printings
/// above all.
///
/// The free catalogue is English-only, so a Japanese card could previously only be
/// typed in by hand: no number, no set, no image, and no price ever. Scrydex
/// carries both languages as a proper catalogue with prices attached, which makes
/// a Japanese card behave like any other rather than like a sealed box.
///
/// Language is chosen by a segment in the path — /pokemon/v1/ja/cards — rather
/// than a query parameter, which is why it's baked into the URL builder here.
/// </summary>
public sealed class ScrydexClient(
    IHttpClientFactory httpFactory, SettingsService settings, ILogger<ScrydexClient> log)
{
    private const string Base = "https://api.scrydex.com/pokemon/v1";

    public bool IsConfigured => settings.GetScrydexCredentials() is not null;

    /// <summary>
    /// Confirms the credentials work. Scrydex answers 401/403 on bad ones, so
    /// unlike the pokemontcg.io key this is a real yes or no.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            return await GetAsync($"{Base}/en/cards?q=name:pikachu&page_size=1", ct) is not null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Scrydex credential check failed");
            return false;
        }
    }

    /// <summary>
    /// Searches one language. Prices are asked for up front because the whole
    /// point is to value these, and a second round trip per card would burn
    /// credits for nothing.
    /// </summary>
    public async Task<IReadOnlyList<ScrydexCard>> SearchAsync(
        string query, string language, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsConfigured) return [];

        var lang = Language(language);
        var url = $"{Base}/{lang}/cards"
                  + $"?q={Uri.EscapeDataString(query)}"
                  + $"&page_size={Math.Clamp(pageSize, 1, 100)}"
                  + "&include=prices";

        var root = await GetAsync(url, ct);
        if (root is null) return [];

        if (!root.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray().Select(c => Map(c, lang)).ToList();
    }

    /// <summary>One card by id, for refreshing something already owned.</summary>
    public async Task<ScrydexCard?> GetCardAsync(string id, string language, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var lang = Language(language);
        var root = await GetAsync($"{Base}/{lang}/cards/{Uri.EscapeDataString(id)}?include=prices", ct);
        if (root is null) return null;

        // A single-card fetch may return the object directly or wrapped in data.
        var card = root.Value.TryGetProperty("data", out var d)
            ? (d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().FirstOrDefault() : d)
            : root.Value;

        return card.ValueKind == JsonValueKind.Object ? Map(card, lang) : null;
    }

    private static string Language(string? language)
        => language?.Trim().ToLowerInvariant() is "ja" or "japanese" ? "ja" : "en";

    private static ScrydexCard Map(JsonElement card, string language)
    {
        var (market, low, currency) = ReadPrices(card);
        var expansion = card.TryGetProperty("expansion", out var e) && e.ValueKind == JsonValueKind.Object
            ? e
            : default;

        return new ScrydexCard(
            Id: Str(card, "id") ?? "",
            Name: Str(card, "name") ?? "",
            Number: Str(card, "number"),
            Rarity: Str(card, "rarity"),
            Supertype: Str(card, "supertype"),
            Subtypes: Strings(card, "subtypes"),
            Types: Strings(card, "types"),
            Hp: Str(card, "hp"),
            Artist: Str(card, "artist"),
            ExpansionId: Str(expansion, "id"),
            ExpansionName: Str(expansion, "name"),
            ReleaseDate: Str(expansion, "release_date") ?? Str(expansion, "releaseDate"),
            ImageSmall: Image(card, "small"),
            ImageLarge: Image(card, "large"),
            Language: language,
            Market: market,
            Low: low,
            Currency: currency);
    }

    /// <summary>
    /// Digs a raw market price out of the response.
    ///
    /// UNVERIFIED against a live response — Scrydex's public docs describe prices
    /// as available via include=prices, and that they cover raw-by-condition and
    /// graded, but do not publish the field names. So this reads defensively: it
    /// walks any "prices" array it can find, whether hanging off the card or off
    /// each variant, and accepts several plausible spellings for the value.
    ///
    /// Graded entries are skipped deliberately. A PSA 10 price is not what a raw
    /// card in a binder is worth, and quietly valuing a collection at graded prices
    /// would inflate it enormously.
    ///
    /// If this returns nothing once a real key is in place, this is the function to
    /// correct, and the shape of one response is all it takes.
    /// </summary>
    private static (double? Market, double? Low, string Currency) ReadPrices(JsonElement card)
    {
        var currency = "USD";
        double? market = null;
        double? low = null;

        foreach (var price in PriceEntries(card))
        {
            // Anything carrying a grade is a graded price; a raw card isn't worth that.
            if (price.TryGetProperty("grade", out var g) && g.ValueKind is not JsonValueKind.Null
                && !string.IsNullOrWhiteSpace(g.ToString()))
                continue;
            if (Str(price, "type")?.Contains("graded", StringComparison.OrdinalIgnoreCase) == true) continue;

            if (Str(price, "currency") is { Length: > 0 } c) currency = c;

            market ??= Num(price, "market") ?? Num(price, "trend") ?? Num(price, "mid") ?? Num(price, "value");
            low ??= Num(price, "low");

            if (market is not null && low is not null) break;
        }

        return (market, low, currency);
    }

    private static IEnumerable<JsonElement> PriceEntries(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object) yield break;

        if (card.TryGetProperty("prices", out var direct) && direct.ValueKind == JsonValueKind.Array)
            foreach (var p in direct.EnumerateArray()) yield return p;

        if (card.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
            foreach (var v in variants.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Object
                    && v.TryGetProperty("prices", out var vp)
                    && vp.ValueKind == JsonValueKind.Array)
                    foreach (var p in vp.EnumerateArray())
                        yield return p;
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        if (settings.GetScrydexCredentials() is not { } creds) return null;

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", creds.ApiKey);
            request.Headers.Add("X-Team-ID", creds.TeamId);

            using var http = CreateClient();
            using var res = await http.SendAsync(request, ct);

            if (IsTransient(res.StatusCode) && attempt < maxAttempts)
            {
                var wait = TimeSpan.FromSeconds(attempt * 1.5);
                log.LogWarning("Scrydex returned {Status}, retry {Attempt}/{Max} in {Seconds}s",
                    (int)res.StatusCode, attempt, maxAttempts, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                // Credits are finite and metered, so a rejection is worth saying out
                // loud rather than silently returning nothing.
                log.LogWarning("Scrydex request failed with {Status}", (int)res.StatusCode);
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CardVault");
        return client;
    }

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static string? Image(JsonElement card, string size)
    {
        if (!card.TryGetProperty("images", out var images)) return null;

        // Either an object of sizes, or a list of image objects.
        if (images.ValueKind == JsonValueKind.Object) return Str(images, size);
        if (images.ValueKind == JsonValueKind.Array)
            foreach (var i in images.EnumerateArray())
                if (Str(i, size) is { Length: > 0 } url)
                    return url;

        return null;
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    private static double? Num(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static string[] Strings(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
