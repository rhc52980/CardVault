using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using CardVault.Services.PriceSources;

// Content root, which decides where wwwroot and appsettings are found.
//
// Published output puts wwwroot beside the binary, and a service starts with its
// working directory in system32, so there it must be pinned to the binary's own
// folder. A dev run is the opposite case: wwwroot lives in the project folder and
// the SDK maps it from there, so pinning it to bin/Debug makes every page 404.
// Detecting which layout we're in covers both.
//
// It also has to be set through WebApplicationOptions rather than
// builder.Host.UseContentRoot() afterwards — that throws at startup the moment
// the two paths actually differ, which is precisely the service case.
var publishedContent = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(publishedContent) ? AppContext.BaseDirectory : null,
});

// Lets the same binary run as a Windows service, a systemd unit, or straight
// from a terminal — both calls are no-ops when not started that way.
builder.Host.UseWindowsService(o => o.ServiceName = "CardVault");
builder.Host.UseSystemd();

// App version, from <Version> in the csproj. Release builds append a source
// stamp, which is what distinguishes two builds of the same version.
var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";

var buildDate = Assembly.GetExecutingAssembly()
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "";

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Data lives outside the application folder so replacing the app on update can't
// take the collection with it. DataPaths also migrates any older in-app copy.
using var pathsLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var paths = new DataPaths(builder.Configuration, pathsLoggerFactory.CreateLogger<DataPaths>());

var db = new Db(paths);
db.Initialize();

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<CardCache>();
builder.Services.AddSingleton<CollectionService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<BackupService>();

// Startup alone can't keep a promise of daily backups on a server that stays up.
builder.Services.AddHostedService<BackupScheduler>();

builder.Services.AddHttpClient<PokemonTcgClient>(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(30));

// Price sources. Registration order is the preference order used for valuation
// when no source is chosen in Settings. Adding a market means adding a line here.
builder.Services.AddSingleton<IPriceSource, TcgPlayerPriceSource>();
builder.Services.AddSingleton<IPriceSource, CardmarketPriceSource>();

// eBay is a singleton so its OAuth token survives between calls; unlike the other
// two it goes to the network and needs credentials, and it only prices the custom
// items the catalogue knows nothing about.
builder.Services.AddSingleton<EbayClient>();

// Scrydex: the catalogue that has Japanese cards, which the free English-only
// API cannot offer at all.
builder.Services.AddSingleton<ScrydexClient>();
builder.Services.AddSingleton<IPriceSource, EbayPriceSource>();

builder.Services.AddSingleton<PriceSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceSnapshotService>());
builder.Services.AddSingleton<ImportBatchService>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<SetsService>();
builder.Services.AddSingleton<SalesService>();
builder.Services.AddSingleton<WantsService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new UpdateChecker(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<SettingsService>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<UpdateChecker>>(),
    version));
builder.Services.AddSingleton<ExportService>();

// The optional offline catalogue. Singleton because a download runs in the
// background and its progress has to survive between polls.
builder.Services.AddSingleton<CatalogueService>();
builder.Services.AddHttpClient<CustomItemService>(c => c.Timeout = TimeSpan.FromSeconds(30));

// Serialize enums as names so the UI reads "Ambiguous" rather than 1.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Bind on all interfaces so a phone or tablet on the same wifi can browse the
// collection too. Override with ASPNETCORE_URLS if you'd rather keep it local-only.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5188");

var app = builder.Build();

app.Logger.LogInformation("CardVault {Version}{Built} starting", version,
    string.IsNullOrEmpty(buildDate) ? "" : $" (built {buildDate} UTC)");

// Take a backup before anything else touches the data — this is the moment an
// update would otherwise be able to do damage.
app.Services.GetRequiredService<BackupService>().RunStartupBackup();

if (!app.Services.GetRequiredService<SettingsService>().GetApiKeyStatus().Configured)
{
    app.Logger.LogWarning(
        "No pokemontcg.io API key configured — requests will be heavily rate limited. " +
        "Add one on the Settings tab, or in server/appsettings.Local.json under PokemonTcg:ApiKey.");
}

// pokemontcg.io goes down and times out fairly often. When it does, say so plainly
// instead of dumping a 500 — everything already in the vault keeps working.
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var upstreamDown = error is HttpRequestException or TaskCanceledException or TimeoutException;

    ctx.Response.StatusCode = upstreamDown
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";

    await ctx.Response.WriteAsJsonAsync(new
    {
        error = upstreamDown
            ? "pokemontcg.io isn't responding right now. Your collection is unaffected — try again in a moment."
            : "Something went wrong handling that request.",
    });
}));

// index.html must never be cached, but the hashed asset files should be cached hard.
// Vite fingerprints every bundle, so a stale index.html points at a filename that no
// longer exists after an update — which shows up as a blank or half-broken app until
// the browser is force-refreshed.
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-cache, no-store, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
            return;
        }

        // Only the fingerprinted bundles may be cached hard. Vite puts a content
        // hash in those filenames, so the URL genuinely never serves different
        // bytes and "immutable" is a promise we can keep.
        //
        // Everything else under wwwroot keeps its name for the life of the app —
        // logo.png, favicon.ico, the manifest icons. Caching those as immutable
        // told every browser to hold them for a year AND not to revalidate, which
        // meant replacing the artwork changed nothing for anyone who had already
        // loaded the old one. They revalidate instead: small files, and a 304 on
        // an unchanged one costs almost nothing.
        var path = ctx.Context.Request.Path.Value ?? "";
        var fingerprinted = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);

        headers.CacheControl = fingerprinted
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    },
};

app.UseDefaultFiles();
app.UseStaticFiles(staticFileOptions);

// Everything that reveals or changes collection data requires a session once a
// password is set. The static pages stay open because the login screen itself has
// to load, and they contain nothing but the app shell.
app.Use(async (ctx, next) =>
{
    var auth = ctx.RequestServices.GetRequiredService<AuthService>();
    var path = ctx.Request.Path;

    var isProtected = path.StartsWithSegments("/api") || path.StartsWithSegments("/img");
    var isAuthEndpoint = path.StartsWithSegments("/api/auth");

    if (!auth.IsEnabled || !isProtected || isAuthEndpoint)
    {
        await next();
        return;
    }

    if (auth.ValidateSession(ctx.Request.Cookies[AuthService.CookieName]))
    {
        await next();
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new { error = "Sign in to continue." });
});

// ---------------------------------------------------------------- card search

app.MapGet("/api/search", async (
    PokemonTcgClient api,
    CardCache cache,
    CollectionService collection,
    WantsService wants,
    CatalogueService catalogue,
    CancellationToken ct,
    string? q = null,
    string? set = null,
    // Paging must have defaults. Declared as bare ints, a request that omits them
    // is rejected by model binding with an empty 400 before the handler ever runs
    // — a baffling response to `/api/search?q=charizard`.
    int page = 1,
    int pageSize = 24) =>
{
    var intent = SearchQuery.Parse(q, set);

    // The offline catalogue answers first when it's switched on and can express the
    // query. This is the whole point of it: pokemontcg.io averages ten seconds on the
    // searches that succeed and fails most of the rest, and that lands exactly when
    // you're stood there with a card in your hand.
    if (catalogue.IsUsable && intent.CanRunLocally)
    {
        var found = catalogue.Search(intent, Math.Clamp(pageSize is 0 ? 24 : pageSize, 1, 100));
        var ownedLocal = collection.List()
            .GroupBy(i => i.CardId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var wantedLocal = wants.WantedIds();

        return Results.Ok(new
        {
            data = found.Select(c => SummarizeCatalogue(c, ownedLocal, wantedLocal)).ToList(),
            totalCount = found.Count,
            page = 1,
            source = "catalogue",
        });
    }

    var query = SearchQuery.Build(q, set);
    if (query is null) return Results.Ok(new { data = Array.Empty<object>(), totalCount = 0, page = 1 });

    var res = await api.SearchCardsAsync(query, Math.Max(1, page), Math.Clamp(pageSize is 0 ? 24 : pageSize, 1, 100), ct);
    if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        return Results.Ok(new { data = Array.Empty<object>(), totalCount = 0, page = 1 });

    var cards = data.EnumerateArray().Select(c => c.Clone()).ToList();
    cache.UpsertMany(cards);

    // So the UI can badge results you already own or are hunting for.
    var owned = collection.List()
        .GroupBy(i => i.CardId)
        .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
    var wanted = wants.WantedIds();

    var results = cards.Select(c => Summarize(c, owned, wanted)).ToList();

    return Results.Ok(new
    {
        data = results,
        totalCount = res.TryGetProperty("totalCount", out var tc) ? tc.GetInt32() : results.Count,
        page = res.TryGetProperty("page", out var p) ? p.GetInt32() : 1,
    });
});

app.MapGet("/api/cards/{id}", async (string id, PokemonTcgClient api, CardCache cache, CancellationToken ct) =>
{
    var payload = cache.GetPayload(id);
    if (payload is null)
    {
        // A custom item only ever exists locally, so there's nothing upstream to ask
        // for — going to the API would just burn four retries on a guaranteed miss.
        if (CustomItemService.IsCustomId(id)) return Results.NotFound();

        var fetched = await api.GetCardAsync(id, ct);
        if (fetched is null) return Results.NotFound();
        cache.Upsert(fetched.Value);
        payload = fetched.Value.GetRawText();
    }

    return Results.Content(payload, "application/json");
});

// ------------------------------------------------------- sets & set completion

app.MapGet("/api/cards/{id}/history", (
    string id, PriceSnapshotService snapshots, CollectionService collection) =>
{
    var series = snapshots.HistoryFor(id);

    var owned = collection.List()
        .Where(i => i.CardId == id)
        .Select(i => i.Variant)
        .Distinct()
        .ToList();

    return Results.Ok(new CardHistory(id, series, owned));
});

app.MapGet("/api/sets", async (SetsService sets, CancellationToken ct)
    => Results.Ok(await sets.ListAsync(ct)));

app.MapGet("/api/sets/{setId}/cards", async (string setId, SetsService sets, CancellationToken ct) =>
{
    var cards = await sets.CardsInSetAsync(setId, ct);
    return cards.Count == 0 ? Results.NotFound(new { error = $"No cards found for set '{setId}'." }) : Results.Ok(cards);
});

// ------------------------------------------------------------------ collection

app.MapGet("/api/collection", (CollectionService collection) => Results.Ok(collection.List()));

app.MapGet("/api/collection/stats", (CollectionService collection) => Results.Ok(collection.Stats()));

app.MapPost("/api/collection", async (
    AddEntryRequest req,
    CollectionService collection,
    CatalogueService catalogue,
    CardCache cache,
    PokemonTcgClient api,
    PriceSnapshotService snapshots,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CardId)) return Results.BadRequest(new { error = "cardId is required" });

    // The card must exist locally before we can join against it for prices.
    if (!cache.Has(req.CardId))
    {
        // The offline catalogue first when it's available. Adding a card is the one
        // moment that has to be instant and cannot be allowed to fail on a 502, so
        // this deliberately does not call the API even to "enrich" the record — the
        // daily refresh already re-fetches every owned card and will fill in the real
        // printings and prices on its next run.
        var seeded = catalogue.IsUsable ? catalogue.BuildPayload(req.CardId) : null;
        if (seeded is not null)
        {
            cache.Upsert(seeded.Value);

            // Then go and get the real thing, without making anyone wait for it. The
            // card is already in the collection; this fills in its price and its true
            // printings a few seconds later, and costs nothing if it fails.
            snapshots.QueueEnrich(req.CardId);
        }
        else
        {
            var fetched = await api.GetCardAsync(req.CardId, ct);
            if (fetched is null) return Results.NotFound(new { error = $"Unknown card '{req.CardId}'" });
            cache.Upsert(fetched.Value);
        }
    }

    var id = collection.Add(req);

    // Seed today's price so the card's chart isn't empty until the next daily run.
    await snapshots.RecordCurrentPricesAsync(req.CardId, ct);

    return Results.Created($"/api/collection/{id}", new { id });
});

app.MapPatch("/api/collection/{id:long}", (long id, UpdateEntryRequest req, CollectionService collection)
    => collection.Update(id, req) ? Results.NoContent() : Results.NotFound());

app.MapDelete("/api/collection/{id:long}", (long id, CollectionService collection, CustomItemService custom) =>
{
    if (!collection.Delete(id)) return Results.NotFound();
    custom.CleanUpOrphans();
    return Results.NoContent();
});

/// The same edit applied to a whole selection. One request rather than one per card:
/// setting the location on a shelf of two hundred is an ordinary thing to want, and
/// two hundred round trips would be slower and could half-finish.
app.MapPatch("/api/collection/bulk", (BulkUpdateRequest req, CollectionService collection) =>
{
    if (req.Ids.Count == 0) return Results.BadRequest(new { error = "No cards were selected." });

    var changed = collection.UpdateMany(req.Ids, req.Update);
    return Results.Ok(new { changed });
});

/// Removing a selection. A POST rather than a DELETE because it carries a body, and
/// enough clients and proxies quietly drop a body on DELETE to make that a bad bet.
app.MapPost("/api/collection/bulk/remove", (
    BulkRemoveRequest req, CollectionService collection, CustomItemService custom) =>
{
    if (req.Ids.Count == 0) return Results.BadRequest(new { error = "No cards were selected." });

    var removed = collection.DeleteMany(req.Ids);
    custom.CleanUpOrphans();
    return Results.Ok(new { removed });
});

// ------------------------------------------------------------ authentication

app.MapGet("/api/auth/status", (HttpContext ctx, AuthService auth) => Results.Ok(new
{
    enabled = auth.IsEnabled,
    authenticated = !auth.IsEnabled || auth.ValidateSession(ctx.Request.Cookies[AuthService.CookieName]),
    // Surfaced so the UI can warn when a password is being sent over plain HTTP.
    isSecureConnection = ctx.Request.IsHttps,
}));

app.MapPost("/api/auth/setup", (HttpContext ctx, PasswordRequest req, AuthService auth) =>
{
    var (ok, error) = auth.SetInitialPassword(req.Password ?? "");
    if (!ok) return Results.BadRequest(new { error });

    IssueSessionCookie(ctx, auth);
    return Results.Ok(new { enabled = true });
});

app.MapPost("/api/auth/login", (HttpContext ctx, PasswordRequest req, AuthService auth) =>
{
    if (!auth.IsEnabled) return Results.BadRequest(new { error = "No password is set." });

    var client = ClientKey(ctx);
    if (auth.LockoutRemaining(client) is { } wait)
    {
        return Results.Json(
            new { error = $"Too many attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (!auth.VerifyPassword(req.Password ?? ""))
    {
        auth.RecordFailure(client);
        return Results.Json(new { error = "Wrong password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    auth.ClearFailures(client);
    IssueSessionCookie(ctx, auth);
    return Results.Ok(new { authenticated = true });
});

app.MapPost("/api/auth/logout", (HttpContext ctx, AuthService auth) =>
{
    if (ctx.Request.Cookies[AuthService.CookieName] is { } token) auth.RevokeSession(token);
    ctx.Response.Cookies.Delete(AuthService.CookieName);
    return Results.Ok(new { authenticated = false });
});

app.MapPost("/api/auth/password", (HttpContext ctx, ChangePasswordRequest req, AuthService auth) =>
{
    var (ok, error) = auth.ChangePassword(req.CurrentPassword ?? "", req.NewPassword ?? "");
    if (!ok) return Results.BadRequest(new { error });

    // Every session was just revoked, including this one — hand back a fresh one so
    // changing your password doesn't sign you out of the device you're using.
    IssueSessionCookie(ctx, auth);
    return Results.Ok(new { changed = true });
});

app.MapPost("/api/auth/disable", (HttpContext ctx, PasswordRequest req, AuthService auth) =>
{
    var (ok, error) = auth.Disable(req.Password ?? "");
    if (!ok) return Results.BadRequest(new { error });

    ctx.Response.Cookies.Delete(AuthService.CookieName);
    return Results.Ok(new { enabled = false });
});

app.MapGet("/api/auth/sessions", (HttpContext ctx, AuthService auth)
    => Results.Ok(auth.ListSessions(ctx.Request.Cookies[AuthService.CookieName])));

app.MapPost("/api/auth/sessions/revoke-others", (HttpContext ctx, AuthService auth) =>
{
    auth.RevokeAllSessions();
    IssueSessionCookie(ctx, auth);
    return Results.Ok(new { revoked = true });
});

// ---------------------------------------------------------------- want list

app.MapGet("/api/wants", (WantsService wants) => Results.Ok(wants.List()));

app.MapPost("/api/wants", async (
    AddWantRequest req, WantsService wants, CardCache cache, PokemonTcgClient api,
    PriceSnapshotService snapshots, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CardId)) return Results.BadRequest(new { error = "cardId is required" });

    if (!cache.Has(req.CardId))
    {
        var fetched = await api.GetCardAsync(req.CardId, ct);
        if (fetched is null) return Results.NotFound(new { error = $"Unknown card '{req.CardId}'" });
        cache.Upsert(fetched.Value);
    }

    var id = wants.Add(req);

    // Start its price history now, same as owning it would.
    await snapshots.RecordCurrentPricesAsync(req.CardId, ct);

    return Results.Created($"/api/wants/{id}", new { id });
});

app.MapPatch("/api/wants/{id:long}", (long id, UpdateWantRequest req, WantsService wants)
    => wants.Update(id, req) ? Results.NoContent() : Results.NotFound());

app.MapDelete("/api/wants/{id:long}", (long id, WantsService wants)
    => wants.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/wants/{id:long}/acquire", (long id, AddEntryRequest req, WantsService wants) =>
{
    var (ok, error, entryId) = wants.Acquire(id, req);
    return ok ? Results.Ok(new { entryId }) : Results.BadRequest(new { error });
});

// ------------------------------------------------------------- sales & export

app.MapPost("/api/collection/{id:long}/sell", (long id, SellRequest req, SalesService sales) =>
{
    var (ok, error, saleId) = sales.Sell(id, req);
    return ok ? Results.Ok(new { saleId }) : Results.BadRequest(new { error });
});

app.MapGet("/api/sales", (SalesService sales) => Results.Ok(sales.List()));

app.MapDelete("/api/sales/{id:long}", (long id, SalesService sales)
    => sales.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/export/collection.csv", (ExportService export) => Results.File(
    System.Text.Encoding.UTF8.GetBytes(export.CollectionCsv()), "text/csv",
    $"card-vault-collection-{DateTime.Now:yyyy-MM-dd}.csv"));

app.MapGet("/api/export/sales.csv", (ExportService export) => Results.File(
    System.Text.Encoding.UTF8.GetBytes(export.SalesCsv()), "text/csv",
    $"card-vault-sales-{DateTime.Now:yyyy-MM-dd}.csv"));

app.MapGet("/api/export/vault.json", (ExportService export) => Results.File(
    System.Text.Encoding.UTF8.GetBytes(export.EverythingJson()), "application/json",
    $"card-vault-{DateTime.Now:yyyy-MM-dd}.json"));

// The collection as something a deck can be built from — energy costs, evolution
// lines, retreat and format legality, which the full export leaves out. ?format=
// standard or expanded narrows it to what's legal there.
app.MapGet("/api/export/deck-inventory.json", (ExportService export, string? format = null) => Results.File(
    System.Text.Encoding.UTF8.GetBytes(export.DeckInventoryJson(format)), "application/json",
    $"card-vault-deck-inventory-{DateTime.Now:yyyy-MM-dd}.json"));

// ------------------------------------------------------- settings & data safety

app.MapGet("/api/settings", async (
    SettingsService settings, DataPaths paths, BackupService backups,
    UpdateChecker updates, CancellationToken ct) =>
{
    // Opening Settings is the natural moment to look. Internally rate limited to
    // once a day, and a no-op unless you've turned it on.
    await updates.MaybeCheckAsync(ct);

    return Results.Ok(new
    {
        vaultName = settings.VaultName,
        apiKey = settings.GetApiKeyStatus(),
        ebay = settings.GetEbayStatus(),
        scrydex = settings.GetScrydexStatus(),
        dataDirectory = paths.Root,
        migratedFromLegacy = paths.MigratedFromLegacy,
        legacyDirectory = paths.MigratedFrom ?? DataPaths.LegacyDirectory,
        backups = backups.List(),
        version = updates.CurrentVersion,
        buildDate,
        update = new
        {
            enabled = updates.Enabled,
            available = updates.UpdateAvailable,
            latest = updates.LatestVersion,
            releaseUrl = updates.ReleaseUrl,
            lastCheckedUtc = updates.LastCheckedUtc,
        },
    });
});

app.MapPost("/api/settings/update-check", async (
    ToggleRequest req, UpdateChecker updates, CancellationToken ct) =>
{
    updates.SetEnabled(req.Enabled);
    await updates.MaybeCheckAsync(ct);

    return Results.Ok(new
    {
        enabled = updates.Enabled,
        available = updates.UpdateAvailable,
        latest = updates.LatestVersion,
        releaseUrl = updates.ReleaseUrl,
        lastCheckedUtc = updates.LastCheckedUtc,
    });
});

// Blank is a legitimate value here — it means "go back to the default name" — so
// this deliberately doesn't reject an empty string the way the API key does.
app.MapPut("/api/settings/vault-name", (VaultNameRequest req, SettingsService settings) =>
{
    settings.VaultName = req.Name ?? "";
    return Results.Ok(new { vaultName = settings.VaultName });
});

app.MapPut("/api/settings/api-key", async (
    ApiKeyRequest req, SettingsService settings, PokemonTcgClient api, CancellationToken ct) =>
{
    var key = req.ApiKey?.Trim();
    if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Enter a key first." });

    // The key is saved regardless: pokemontcg.io has no way to tell us whether a key
    // is genuine, so refusing to save on a failed probe would only ever be wrong for
    // the wrong reason. All we can report is whether the service answered.
    settings.SetApiKey(key);

    bool reachable;
    try
    {
        reachable = await api.TestConnectionAsync(key, ct);
    }
    catch
    {
        reachable = false;
    }

    return Results.Ok(new
    {
        saved = true,
        reachable,
        apiKey = settings.GetApiKeyStatus(),
    });
});

app.MapDelete("/api/settings/api-key", (SettingsService settings) =>
{
    settings.ClearApiKey();
    return Results.Ok(new { apiKey = settings.GetApiKeyStatus() });
});

app.MapPut("/api/settings/ebay", async (
    EbayCredentialsRequest req, SettingsService settings, EbayClient ebay, CancellationToken ct) =>
{
    var id = req.ClientId?.Trim();
    var secret = req.ClientSecret?.Trim();

    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        return Results.BadRequest(new { error = "Both the App ID and the Cert ID are required." });

    // Saved before testing, same as the pokemontcg key: the check needs the stored
    // pair to run at all, and a working credential you typed correctly shouldn't be
    // thrown away because eBay happened to be unreachable.
    settings.SetEbayCredentials(id, secret);

    // Unlike pokemontcg.io, eBay does tell us: bad credentials can't mint a token,
    // so this is a real yes or no rather than "the service answered".
    var reachable = await ebay.TestConnectionAsync(ct);

    return Results.Ok(new
    {
        saved = true,
        reachable,
        ebay = settings.GetEbayStatus(),
    });
});

app.MapDelete("/api/settings/ebay", (SettingsService settings) =>
{
    settings.ClearEbayCredentials();
    return Results.Ok(new { ebay = settings.GetEbayStatus() });
});

app.MapPut("/api/settings/scrydex", async (
    ScrydexCredentialsRequest req, SettingsService settings, ScrydexClient scrydex, CancellationToken ct) =>
{
    var key = req.ApiKey?.Trim();
    var team = req.TeamId?.Trim();

    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(team))
        return Results.BadRequest(new { error = "Both the API key and the Team ID are required." });

    settings.SetScrydexCredentials(key, team);

    // Scrydex rejects bad credentials outright, so this is a real answer rather
    // than "the service replied".
    var reachable = await scrydex.TestConnectionAsync(ct);

    return Results.Ok(new { saved = true, reachable, scrydex = settings.GetScrydexStatus() });
});

app.MapDelete("/api/settings/scrydex", (SettingsService settings) =>
{
    settings.ClearScrydexCredentials();
    return Results.Ok(new { scrydex = settings.GetScrydexStatus() });
});

/// A one-off look at exactly what Scrydex returns, so the price mapping can be
/// checked against a real response rather than against the documentation, which
/// does not publish the field names inside a price entry.
///
/// Both halves matter. "raw" is the untouched response and is the only place the
/// price field names appear; "mapped" is what the client currently makes of it. A
/// mapped price of null beside a raw response full of figures is the whole answer:
/// the reader is looking for the wrong names.
app.MapGet("/api/settings/scrydex/probe", async (
    ScrydexClient scrydex, CancellationToken ct, string q = "pikachu", string language = "ja") =>
{
    if (!scrydex.IsConfigured) return Results.BadRequest(new { error = "No Scrydex credentials saved." });

    var raw = await scrydex.ProbeAsync(q, language, ct);
    if (raw is null)
        return Results.BadRequest(new { error = "Scrydex did not answer. Check the credentials and the log." });

    return Results.Ok(new
    {
        // Mapped from the response beside it, not fetched again: two calls could
        // answer with different cards, which would break the comparison exactly when
        // it's needed.
        mapped = ScrydexClient.MapResponse(raw.Value, language),
        // Named so it reads as the point of the endpoint rather than a debug leftover.
        raw,
    });
});

// ------------------------------------------------------------ offline catalogue

app.MapGet("/api/catalogue", (CatalogueService catalogue) => Results.Ok(catalogue.Status()));

app.MapPost("/api/catalogue/download", (CatalogueDownloadRequest? req, CatalogueService catalogue) =>
{
    if (!catalogue.Start(req?.IncludeImages ?? true))
        return Results.Conflict(new { error = "A download is already running." });

    return Results.Accepted("/api/catalogue", catalogue.Status());
});

app.MapPost("/api/catalogue/cancel", (CatalogueService catalogue) =>
{
    catalogue.Cancel();
    return Results.Ok(catalogue.Status());
});

app.MapPut("/api/catalogue/enabled", (ToggleRequest req, CatalogueService catalogue) =>
{
    catalogue.SetEnabled(req.Enabled);
    return Results.Ok(catalogue.Status());
});

app.MapDelete("/api/catalogue", (CatalogueService catalogue) =>
{
    catalogue.Delete();
    return Results.Ok(catalogue.Status());
});

app.MapPost("/api/backups", (BackupService backups) => Results.Ok(backups.Create("manual")));

app.MapGet("/api/backups/{name}", (string name, BackupService backups) =>
{
    var path = backups.ResolvePath(name);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "application/octet-stream", Path.GetFileName(path));
});

app.MapDelete("/api/backups/{name}", (string name, BackupService backups)
    => backups.Delete(name) ? Results.NoContent() : Results.NotFound());

// ------------------------------------------- manual entry: sealed, slabs, oddities

app.MapPost("/api/custom", async (
    HttpRequest request, CustomItemService custom, PriceSnapshotService snapshots, CancellationToken ct) =>
{
    CustomItemRequest? req;
    Stream? image = null;

    // Accepts a multipart form when an image file is attached, plain JSON otherwise.
    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        string? F(string k) => form.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;
        double? D(string k) => double.TryParse(F(k), out var d) ? d : null;

        req = new CustomItemRequest(
            Name: F("name") ?? "",
            Category: F("category"),
            ImageUrl: F("imageUrl"),
            Quantity: int.TryParse(F("quantity"), out var q) ? q : 1,
            Condition: F("condition") ?? "NM",
            Grade: F("grade"),
            Value: D("value"),
            PurchasePrice: D("purchasePrice"),
            PurchaseDate: F("purchaseDate"),
            Notes: F("notes"));

        image = form.Files.GetFile("image")?.OpenReadStream();
    }
    else
    {
        req = await request.ReadFromJsonAsync<CustomItemRequest>(ct);
    }

    if (req is null || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "A name is required." });

    var (cardId, entryId) = await custom.CreateAsync(req, image, ct);

    // Ask eBay what this is going for straight away, the same courtesy a catalogue
    // card gets. For sealed product and slabs this is the only price there'll ever
    // be, so waiting a day for the first one is a poor first impression.
    await snapshots.RecordCurrentPricesAsync(cardId, ct);

    return Results.Created($"/api/collection/{entryId}", new { cardId, entryId });
});

app.MapGet("/api/prices/sources", (PriceSnapshotService snapshots, SettingsService settings) => Results.Ok(new
{
    sources = snapshots.Sources(),
    // Which market drives valuation. Never a blend: the sources report different
    // currencies, so combining them would produce a meaningless number.
    preferred = settings.PreferredPriceSource,
}));

app.MapPut("/api/prices/sources/preferred", (
    PreferredSourceRequest req, PriceSnapshotService snapshots, SettingsService settings) =>
{
    var known = snapshots.Sources().Select(s => s.Id).ToHashSet();
    if (req.Source is null || !known.Contains(req.Source))
        return Results.BadRequest(new { error = "Unknown price source." });

    settings.PreferredPriceSource = req.Source;
    return Results.Ok(new { preferred = settings.PreferredPriceSource });
});

// Starts a refresh and returns immediately. Refreshing is one API call per card
// with pacing between them, so a large collection takes minutes — long enough that
// awaiting it here would just time the request out.
app.MapPost("/api/prices/snapshot", (PriceSnapshotService snapshots) =>
{
    if (!snapshots.StartRefresh())
        return Results.Conflict(new { error = "A price refresh is already running." });

    return Results.Accepted("/api/prices/snapshot", snapshots.RefreshProgress);
});

app.MapGet("/api/prices/snapshot", (PriceSnapshotService snapshots)
    => Results.Ok(snapshots.RefreshProgress));

// -------------------------------------------------------------- CSV bulk import

app.MapPost("/api/import", (ImportRequest req, ImportService import) =>
{
    if (string.IsNullOrWhiteSpace(req.Csv)) return Results.BadRequest(new { error = "No CSV content provided." });

    var job = import.Start(req.Csv);
    return job.State == "failed"
        ? Results.BadRequest(new { error = job.Error })
        : Results.Ok(new { jobId = job.Id, total = job.Total, unmappedColumns = job.UnmappedColumns });
});

app.MapGet("/api/import/{jobId}", (string jobId, ImportService import)
    => import.Get(jobId) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/import/{jobId}/commit", (string jobId, CommitRequest req, ImportService import) =>
{
    if (import.Get(jobId) is null) return Results.NotFound();

    // Returns per-row outcomes as well as the total, so the review list can mark up
    // the rows that landed rather than leaving you to work out which of a hundred
    // cards the count is missing.
    return Results.Ok(import.Commit(jobId, req.Rows));
});

// ------------------------------------------------------------- imports as batches

app.MapGet("/api/imports", (ImportBatchService batches) => Results.Ok(batches.List()));

app.MapGet("/api/imports/{id}", (string id, ImportBatchService batches)
    => batches.Get(id) is { } batch ? Results.Ok(batch) : Results.NotFound());

app.MapPost("/api/imports/{id}/acknowledge", (string id, ImportBatchService batches) =>
{
    batches.Acknowledge(id);
    return batches.Get(id) is { } batch ? Results.Ok(batch) : Results.NotFound();
});

app.MapPost("/api/imports/acknowledge", (ImportBatchService batches)
    => Results.Ok(new { acknowledged = batches.AcknowledgeAll() }));

// Removes the cards an import added. Sales and price history are deliberately left
// alone — see ImportBatchService.Remove for why neither is at risk.
app.MapDelete("/api/imports/{id}", (string id, ImportBatchService batches) =>
{
    var (found, removed) = batches.Remove(id);
    return found ? Results.Ok(new { removed }) : Results.NotFound();
});

app.MapGet("/api/import/template", () =>
{
    // The last two rows are the point of the template as much as the headers: a
    // number written the way the card prints it identifies the set by itself, so
    // the Set column can be left empty and a spreadsheet becomes one column of
    // numbers typed off the cards.
    const string csv =
        "Name,Set,Number,Quantity,Variant,Condition,Language,Purchase Price,Purchase Date,Notes\n" +
        "Charizard,Base,4,1,Holofoil,NM,English,250.00,1999-01-09,Childhood card\n" +
        "Pikachu,Base,58,3,Normal,LP,,4.00,,\n" +
        "Mew,,151,1,Normal,NM,Japanese,,,Left blank above means English\n" +
        ",,045/094,1,,NM,,,,Number as printed - the set is worked out from it\n" +
        ",,45094,1,,NM,,,,The same thing with nothing typed but digits\n";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "card-vault-template.csv");
});

// ---------------------------------------------------------------- card images

app.MapGet("/img/{cardId}/{size}", async (
    string cardId, string size, ImageCache images, CatalogueService catalogue, CancellationToken ct) =>
{
    // The offline catalogue first, and only for thumbnails — it stores one WebP per
    // card, which is the size the grid actually draws. Serving it here is what makes
    // browsing search results touch the network for neither data nor artwork. A large
    // image still goes through the on-demand cache, so opening one card fetches one
    // file rather than the catalogue having to carry every detail scan.
    if (size != "large" && catalogue.ImageFile(cardId) is { } local)
        return Results.File(local, "image/webp", enableRangeProcessing: true);

    var path = await images.GetLocalPathAsync(cardId, size, ct);
    if (path is null) return Results.NotFound();
    return Results.File(path, "image/png", enableRangeProcessing: true);
});

// Same options here, otherwise the SPA fallback would serve a cacheable index.html
// and reintroduce the stale-bundle problem for any deep link.
app.MapFallbackToFile("index.html", staticFileOptions);

app.Run();

// ---------------------------------------------------------------------- helpers

static void IssueSessionCookie(HttpContext ctx, AuthService auth)
{
    var token = auth.CreateSession(ctx.Request.Headers.UserAgent.ToString(), ClientKey(ctx));

    ctx.Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        // Secure is conditional on purpose: forcing it would break sign-in over
        // plain HTTP on a home network, where the browser silently drops the
        // cookie. Over the internet you must be on HTTPS — see the README.
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(30),
        Path = "/",
    });
}

static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>
/// The same shape as <see cref="Summarize"/>, for a card that came from the offline
/// catalogue instead of the API.
///
/// Prices are null and that is deliberate — the catalogue has none to give, and
/// inventing one from a stale figure would be worse than showing nothing. Printings
/// are a guess from rarity and era for the same reason; the daily refresh replaces
/// them with the real list once the card is owned.
/// </summary>
static object SummarizeCatalogue(
    CatalogueCard card, IReadOnlyDictionary<string, int> owned, IReadOnlySet<string> wanted) => new
{
    id = card.Id,
    name = card.Name,
    number = card.Number,
    rarity = card.Rarity,
    supertype = card.Supertype,
    hp = (string?)null,
    artist = card.Artist,
    types = card.Types,
    subtypes = Array.Empty<string>(),
    setId = card.SetId,
    setName = card.SetName,
    setSeries = card.SetSeries,
    releaseDate = card.ReleaseDate,
    imageSmall = card.HasImage ? $"/img/{card.Id}/small" : null,
    imageLarge = (string?)null,
    marketPrice = (double?)null,
    lowPrice = (double?)null,
    highPrice = (double?)null,
    variants = Pricing.LikelyVariants(card.Rarity, card.ReleaseDate),
    pricesUpdatedAt = (string?)null,
    ownedQuantity = owned.TryGetValue(card.Id, out var n) ? n : 0,
    isWanted = wanted.Contains(card.Id),
};

static object Summarize(JsonElement card, IReadOnlyDictionary<string, int> owned, IReadOnlySet<string> wanted)
{
    var id = card.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
    var set = card.TryGetProperty("set", out var s) ? s : default;
    var images = card.TryGetProperty("images", out var im) ? im : default;
    var price = Pricing.ForVariant(card, null);

    return new
    {
        id,
        name = Text(card, "name"),
        number = Text(card, "number"),
        rarity = Text(card, "rarity"),
        supertype = Text(card, "supertype"),
        hp = Text(card, "hp"),
        artist = Text(card, "artist"),
        types = Strings(card, "types"),
        subtypes = Strings(card, "subtypes"),
        setId = Text(set, "id"),
        setName = Text(set, "name"),
        setSeries = Text(set, "series"),
        releaseDate = Text(set, "releaseDate"),
        imageSmall = Text(images, "small"),
        imageLarge = Text(images, "large"),
        marketPrice = price.Market,
        lowPrice = price.Low,
        highPrice = price.High,
        variants = Pricing.AvailableVariants(card),
        pricesUpdatedAt = Pricing.TcgUpdatedAt(card),
        ownedQuantity = owned.TryGetValue(id, out var n) ? n : 0,
        isWanted = wanted.Contains(id),
    };

    static string? Text(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    static string[] Strings(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
