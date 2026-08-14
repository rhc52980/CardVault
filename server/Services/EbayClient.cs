using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CardVault.Services;

/// <summary>One live eBay listing, reduced to the parts a price is made of.</summary>
public sealed record EbayListing(string Title, double Price, string Currency, string? Condition, string? Url);

/// <summary>
/// eBay Browse API, used to put a live number on things the card catalogue has no
/// price for — sealed product and graded slabs.
///
/// Browse returns ACTIVE listings. eBay's sold-price data lives in the Marketplace
/// Insights API, which is Limited Release and closed to new applicants, and the old
/// Finding API findCompletedItems call was shut down in February 2025. So what this
/// client can honestly report is what sellers are asking, not what anything sold
/// for, and everything downstream is labelled accordingly.
/// </summary>
public sealed class EbayClient(IHttpClientFactory httpFactory, SettingsService settings, ILogger<EbayClient> log)
{
    private const string TokenUrl = "https://api.ebay.com/identity/v1/oauth2/token";
    private const string SearchUrl = "https://api.ebay.com/buy/browse/v1/item_summary/search";
    private const string Scope = "https://api.ebay.com/oauth/api_scope";

    /// <summary>
    /// Which eBay site to search. US is where Pokémon singles and sealed product
    /// trade in the deepest volume, and it keeps this source in dollars so it lines
    /// up with TCGplayer rather than introducing a third currency.
    /// </summary>
    private const string Marketplace = "EBAY_US";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private string? _tokenClientId;
    private DateTime _tokenExpiresUtc;

    /// <summary>Whether credentials exist at all — used to skip work and warn in the UI.</summary>
    public bool IsConfigured => settings.GetEbayCredentials() is not null;

    /// <summary>
    /// Confirms the saved credentials can actually mint a token. Unlike the
    /// pokemontcg.io key this is a real check: eBay rejects bad credentials with a
    /// 401, so a success here means the pair genuinely works.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            return await GetTokenAsync(forceRefresh: true, ct) is not null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "eBay credential check failed");
            return false;
        }
    }

    /// <summary>
    /// Listings matching a keyword search, cheapest first, or an empty list when
    /// eBay isn't configured. Fixed-price only: an auction's current bid is a
    /// half-finished number that says little about what the item is worth.
    /// </summary>
    public async Task<IReadOnlyList<EbayListing>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (!IsConfigured) return [];

        var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}"
                  + $"&limit={Math.Clamp(limit, 1, 200)}"
                  + "&sort=price"
                  + $"&filter={Uri.EscapeDataString("buyingOptions:{FIXED_PRICE}")}";

        var root = await GetJsonAsync(url, ct);
        if (root is null) return [];

        if (root.Value.ValueKind != JsonValueKind.Object
            || !root.Value.TryGetProperty("itemSummaries", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            // No matches is the normal answer for an obscure item, not a failure.
            return [];
        }

        var listings = new List<EbayListing>();
        foreach (var item in items.EnumerateArray())
        {
            if (Parse(item) is { } listing) listings.Add(listing);
        }

        return listings;
    }

    /// <summary>
    /// Turns one search result into a listing, or null if it has no usable price.
    ///
    /// Shipping is added when eBay quotes a fixed figure, because a booster box
    /// listed at $1 with $40 postage is not a $1 booster box. Calculated or
    /// unquoted shipping is left out rather than guessed at, which biases those
    /// listings low — the alternative is inventing a number.
    /// </summary>
    private static EbayListing? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Object) return null;
        if (Amount(price) is not { } value) return null;

        var currency = price.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "USD"
            : "USD";

        if (item.TryGetProperty("shippingOptions", out var shipping)
            && shipping.ValueKind == JsonValueKind.Array
            && shipping.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
            && first.TryGetProperty("shippingCost", out var cost)
            && Amount(cost) is { } postage
            && (!cost.TryGetProperty("currency", out var sc)
                || sc.ValueKind != JsonValueKind.String
                || sc.GetString() == currency))
        {
            value += postage;
        }

        return new EbayListing(
            Title: Str(item, "title") ?? "",
            Price: Math.Round(value, 2),
            Currency: currency,
            Condition: Str(item, "condition"),
            Url: Str(item, "itemWebUrl"));
    }

    /// <summary>eBay sends money as a string, so it needs parsing rather than reading.</summary>
    private static double? Amount(JsonElement money)
        => money.ValueKind == JsonValueKind.Object
           && money.TryGetProperty("value", out var v)
           && v.ValueKind == JsonValueKind.String
           && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// GET with a bearer token, refreshing it once on a 401 in case it expired
    /// mid-flight, and backing off on the transient statuses eBay uses for
    /// throttling. Returns null rather than throwing when the call can't be made,
    /// so one failed lookup costs a single price rather than the whole cycle.
    /// </summary>
    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var refreshed = false;

        for (var attempt = 1; ; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: false, ct);
            if (token is null) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", Marketplace);

            using var http = CreateClient();
            using var res = await http.SendAsync(request, ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                // Token rejected: drop it and mint a fresh one before giving up.
                refreshed = true;
                InvalidateToken();
                continue;
            }

            if (IsTransient(res.StatusCode) && attempt < maxAttempts)
            {
                var wait = TimeSpan.FromSeconds(attempt * 1.5);
                log.LogWarning("eBay returned {Status}, retry {Attempt}/{Max} in {Seconds}s",
                    (int)res.StatusCode, attempt, maxAttempts, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("eBay search failed with {Status}", (int)res.StatusCode);
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// A cached application access token, minted on demand.
    ///
    /// Tokens last two hours and eBay rate-limits the token endpoint itself, so
    /// re-minting per request would burn the quota that the searches need. The
    /// cache is keyed on the client id so changing credentials in Settings takes
    /// effect immediately instead of leaving the old application authenticated.
    /// </summary>
    private async Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var credentials = settings.GetEbayCredentials();
        if (credentials is null) return null;

        if (!forceRefresh && IsTokenUsable(credentials.ClientId)) return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed it while we queued for the lock.
            if (!forceRefresh && IsTokenUsable(credentials.ClientId)) return _token;

            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}"));

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = Scope,
            });

            using var http = CreateClient();
            using var res = await http.SendAsync(request, ct);
            if (!res.IsSuccessStatusCode)
            {
                // Almost always a wrong or sandbox-only credential pair. Logged once
                // per attempt rather than thrown, so the snapshot cycle carries on
                // with the sources that do work.
                log.LogWarning("eBay token request failed with {Status}", (int)res.StatusCode);
                InvalidateToken();
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var token = Str(root, "access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                InvalidateToken();
                return null;
            }

            var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt32()
                : 7200;

            _token = token;
            _tokenClientId = credentials.ClientId;
            // A minute of headroom so a token doesn't lapse between check and use.
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// A pooled client per call. This service is a singleton so it can hold the
    /// access token across requests; taking the HttpClient from the factory each
    /// time keeps connection handling where it belongs rather than pinning one
    /// handler open for the life of the app.
    /// </summary>
    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private bool IsTokenUsable(string clientId)
        => _token is not null && _tokenClientId == clientId && DateTime.UtcNow < _tokenExpiresUtc;

    private void InvalidateToken()
    {
        _token = null;
        _tokenClientId = null;
        _tokenExpiresUtc = DateTime.MinValue;
    }

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
