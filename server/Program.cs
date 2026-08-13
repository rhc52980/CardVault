using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using PokemonVault.Data;
using PokemonVault.Models;
using PokemonVault.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

var apiKey = builder.Configuration["PokemonTcg:ApiKey"]
             ?? builder.Configuration["POKEMONTCG_API_KEY"];

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var db = new Db(dataDir);
db.Initialize();

builder.Services.AddSingleton(db);
builder.Services.AddSingleton<CardCache>();
builder.Services.AddSingleton<CollectionService>();

builder.Services.AddHttpClient<PokemonTcgClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrWhiteSpace(apiKey)) c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});

builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<PriceSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceSnapshotService>());
builder.Services.AddSingleton<ImportService>();

// Serialize enums as names so the UI reads "Ambiguous" rather than 1.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Bind on all interfaces so a phone or tablet on the same wifi can browse the
// collection too. Override with ASPNETCORE_URLS if you'd rather keep it local-only.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5188");

var app = builder.Build();

if (string.IsNullOrWhiteSpace(apiKey))
{
    app.Logger.LogWarning(
        "No pokemontcg.io API key configured — requests will be heavily rate limited. " +
        "Add one to server/appsettings.Local.json under PokemonTcg:ApiKey.");
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

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- card search

app.MapGet("/api/search", async (
    string? q,
    string? set,
    int page,
    int pageSize,
    PokemonTcgClient api,
    CardCache cache,
    CollectionService collection,
    CancellationToken ct) =>
{
    var query = BuildQuery(q, set);
    if (query is null) return Results.Ok(new { data = Array.Empty<object>(), totalCount = 0, page = 1 });

    var res = await api.SearchCardsAsync(query, Math.Max(1, page), Math.Clamp(pageSize is 0 ? 24 : pageSize, 1, 100), ct);
    if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        return Results.Ok(new { data = Array.Empty<object>(), totalCount = 0, page = 1 });

    var cards = data.EnumerateArray().Select(c => c.Clone()).ToList();
    cache.UpsertMany(cards);

    // So the UI can badge results you already own.
    var owned = collection.List()
        .GroupBy(i => i.CardId)
        .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    var results = cards.Select(c => Summarize(c, owned)).ToList();

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
        var fetched = await api.GetCardAsync(id, ct);
        if (fetched is null) return Results.NotFound();
        cache.Upsert(fetched.Value);
        payload = fetched.Value.GetRawText();
    }

    return Results.Content(payload, "application/json");
});

app.MapGet("/api/sets", async (PokemonTcgClient api, CancellationToken ct) =>
{
    var res = await api.GetSetsAsync(ct);
    return Results.Content(res.GetRawText(), "application/json");
});

// ------------------------------------------------------------------ collection

app.MapGet("/api/collection", (CollectionService collection) => Results.Ok(collection.List()));

app.MapGet("/api/collection/stats", (CollectionService collection) => Results.Ok(collection.Stats()));

app.MapPost("/api/collection", async (
    AddEntryRequest req,
    CollectionService collection,
    CardCache cache,
    PokemonTcgClient api,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CardId)) return Results.BadRequest(new { error = "cardId is required" });

    // The card must exist locally before we can join against it for prices.
    if (!cache.Has(req.CardId))
    {
        var fetched = await api.GetCardAsync(req.CardId, ct);
        if (fetched is null) return Results.NotFound(new { error = $"Unknown card '{req.CardId}'" });
        cache.Upsert(fetched.Value);
    }

    var id = collection.Add(req);
    return Results.Created($"/api/collection/{id}", new { id });
});

app.MapPatch("/api/collection/{id:long}", (long id, UpdateEntryRequest req, CollectionService collection)
    => collection.Update(id, req) ? Results.NoContent() : Results.NotFound());

app.MapDelete("/api/collection/{id:long}", (long id, CollectionService collection)
    => collection.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/prices/snapshot", async (PriceSnapshotService snapshots, CancellationToken ct) =>
{
    var count = await snapshots.CaptureAsync(ct);
    return Results.Ok(new { captured = count });
});

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
    var added = import.Commit(jobId, req.Rows);
    return Results.Ok(new { added });
});

app.MapGet("/api/import/template", () =>
{
    const string csv =
        "Name,Set,Number,Quantity,Variant,Condition,Purchase Price,Purchase Date,Notes\n" +
        "Charizard,Base,4,1,Holofoil,NM,250.00,1999-01-09,Childhood card\n" +
        "Pikachu,Base,58,3,Normal,LP,4.00,,\n" +
        "Charizard ex,151,6,2,Holofoil,NM,12.50,,\n";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "pokemon-vault-template.csv");
});

// ---------------------------------------------------------------- card images

app.MapGet("/img/{cardId}/{size}", async (
    string cardId, string size, ImageCache images, CancellationToken ct) =>
{
    var path = await images.GetLocalPathAsync(cardId, size, ct);
    if (path is null) return Results.NotFound();
    return Results.File(path, "image/png", enableRangeProcessing: true);
});

app.MapFallbackToFile("index.html");

app.Run();

// ---------------------------------------------------------------------- helpers

/// <summary>
/// Turns whatever the user typed into a pokemontcg.io query. Free text becomes a
/// wildcard name search; anything containing a colon is passed through so power
/// users can write things like <c>rarity:"Rare Holo" types:Fire</c>.
/// </summary>
static string? BuildQuery(string? q, string? set)
{
    var parts = new List<string>();

    if (!string.IsNullOrWhiteSpace(q))
    {
        var trimmed = q.Trim();
        parts.Add(trimmed.Contains(':') ? trimmed : $"name:\"*{trimmed.Replace("\"", "")}*\"");
    }

    if (!string.IsNullOrWhiteSpace(set)) parts.Add($"set.id:{set.Trim()}");

    return parts.Count == 0 ? null : string.Join(" ", parts);
}

static object Summarize(JsonElement card, IReadOnlyDictionary<string, int> owned)
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
