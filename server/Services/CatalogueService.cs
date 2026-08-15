using System.Text.Json;
using CardVault.Data;
using CardVault.Models;
using SkiaSharp;

namespace CardVault.Services;

/// <summary>
/// An optional offline copy of the card catalogue.
///
/// pokemontcg.io is slow and unreliable in a way that lands squarely on the worst
/// possible moment: you're holding a card, you type its number, and you wait — a
/// measured average of ten seconds on the searches that answered at all, with
/// two-thirds returning 500s. Prices can afford that, because they're fetched by a
/// background job once a day. Identifying a card cannot.
///
/// So the catalogue is downloaded once from the pokemon-tcg-data repository and
/// searched locally. It carries no prices and structurally cannot: there is no
/// price block anywhere in the source data. Nothing here has an opinion about what
/// a card is worth — that stays with the price sources, hitting the network on
/// their own schedule.
/// </summary>
public sealed class CatalogueService(
    Db db,
    DataPaths paths,
    SettingsService settings,
    IHttpClientFactory httpFactory,
    ILogger<CatalogueService> log)
{
    private const string DataBase = "https://raw.githubusercontent.com/PokemonTCG/pokemon-tcg-data/master";
    private const string EnabledSetting = "catalogue_enabled";
    private const string DownloadedAtSetting = "catalogue_downloaded_at";

    /// <summary>
    /// Concurrent image fetches. Six is brisk without being rude to a CDN we're
    /// about to ask for twenty thousand files.
    /// </summary>
    private const int ImageConcurrency = 6;

    /// <summary>
    /// WebP quality. The source PNGs are barely compressed — around 153 KB for a
    /// 240x330 thumbnail — and the grid draws them smaller than their own pixel
    /// dimensions, so there is nothing visible to lose here.
    /// </summary>
    private const int WebpQuality = 82;

    private readonly object _lock = new();
    private CancellationTokenSource? _cancellation;
    private CatalogueProgress? _progress;

    /// <summary>
    /// Whether the add-cards flow should prefer the local copy. Off unless it's both
    /// switched on and actually downloaded, so enabling it early can't produce a
    /// search box that finds nothing.
    /// </summary>
    public bool IsUsable => settings.Get(EnabledSetting) == "1" && CardCount() > 0;

    public bool IsRunning => _progress is { State: "sets" or "images" };

    public void SetEnabled(bool enabled) => settings.Set(EnabledSetting, enabled ? "1" : "0");

    public CatalogueStatus Status()
    {
        var (images, bytes) = ImageStats();
        return new CatalogueStatus(
            Enabled: settings.Get(EnabledSetting) == "1",
            Cards: CardCount(),
            Images: images,
            ImageBytes: bytes,
            DownloadedAt: settings.Get(DownloadedAtSetting),
            Progress: _progress);
    }

    // ------------------------------------------------------------------ downloading

    /// <summary>
    /// Starts a download in the background and returns immediately; the caller polls
    /// <see cref="Status"/>. Safe to call when one is already running — it won't
    /// start a second.
    /// </summary>
    public bool Start(bool includeImages)
    {
        lock (_lock)
        {
            if (IsRunning) return false;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _progress = new CatalogueProgress("sets", 0, 0, "Starting", null);
        }

        var ct = _cancellation.Token;
        _ = Task.Run(() => RunAsync(includeImages, ct), CancellationToken.None);
        return true;
    }

    public void Cancel()
    {
        lock (_lock) { _cancellation?.Cancel(); }
    }

    private async Task RunAsync(bool includeImages, CancellationToken ct)
    {
        try
        {
            await DownloadMetadataAsync(ct);
            if (includeImages) await DownloadImagesAsync(ct);

            settings.Set(DownloadedAtSetting, DateTime.UtcNow.ToString("o"));
            _progress = _progress! with { State = "done", Detail = "Finished" };
            log.LogInformation("Catalogue download finished: {Cards} cards", CardCount());
        }
        catch (OperationCanceledException)
        {
            _progress = _progress! with { State = "cancelled", Detail = "Stopped" };
            log.LogInformation("Catalogue download cancelled");
        }
        catch (Exception e)
        {
            _progress = _progress! with { State = "failed", Error = e.Message };
            log.LogError(e, "Catalogue download failed");
        }
    }

    /// <summary>
    /// Pulls every English set and its cards. Around 25 MB across 174 files.
    ///
    /// The set details come from the index rather than the cards: unlike the API
    /// payload, a card in the bulk data carries no set object at all — just an id,
    /// a number and its own attributes — so set name, series and printed total have
    /// to be joined on from here.
    /// </summary>
    private async Task DownloadMetadataAsync(CancellationToken ct)
    {
        using var http = CreateClient();

        _progress = new CatalogueProgress("sets", 0, 0, "Fetching the set list", null);
        var setsJson = await http.GetStringAsync($"{DataBase}/sets/en.json", ct);
        using var setsDoc = JsonDocument.Parse(setsJson);
        var sets = setsDoc.RootElement.EnumerateArray().ToList();

        _progress = _progress with { Total = sets.Count };

        var done = 0;
        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();

            var setId = Str(set, "id");
            if (setId is null) continue;

            var setName = Str(set, "name");
            _progress = _progress with { Done = done, Detail = setName ?? setId };

            string cardsJson;
            try
            {
                cardsJson = await http.GetStringAsync($"{DataBase}/cards/en/{setId}.json", ct);
            }
            catch (HttpRequestException e)
            {
                // A set in the index with no card file yet is a data lag, not a
                // failure worth abandoning the other 173 sets over.
                log.LogWarning(e, "No card data for set {SetId}; skipping", setId);
                done++;
                continue;
            }

            using var cardsDoc = JsonDocument.Parse(cardsJson);
            UpsertSet(setId, set, cardsDoc.RootElement);

            done++;
            _progress = _progress with { Done = done };
        }
    }

    /// <summary>One transaction per set — 174 commits rather than 20,000.</summary>
    private void UpsertSet(string setId, JsonElement set, JsonElement cards)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        // has_image is left alone on conflict so re-downloading metadata doesn't
        // orphan artwork that's already on disk.
        cmd.CommandText = """
            INSERT INTO catalogue (id, name, set_id, set_name, set_series, number, printed_total,
                                   rarity, supertype, subtypes, types, artist, release_date, image_url)
            VALUES ($id, $name, $setId, $setName, $setSeries, $number, $printedTotal,
                    $rarity, $supertype, $subtypes, $types, $artist, $releaseDate, $imageUrl)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, set_name=excluded.set_name, set_series=excluded.set_series,
                number=excluded.number, printed_total=excluded.printed_total, rarity=excluded.rarity,
                supertype=excluded.supertype, subtypes=excluded.subtypes, types=excluded.types,
                artist=excluded.artist, release_date=excluded.release_date, image_url=excluded.image_url
            """;

        var p = cmd.Parameters;
        foreach (var key in new[] { "$id", "$name", "$setId", "$setName", "$setSeries", "$number",
                                    "$printedTotal", "$rarity", "$supertype", "$subtypes", "$types",
                                    "$artist", "$releaseDate", "$imageUrl" })
            p.AddWithValue(key, DBNull.Value);

        var setName = Str(set, "name");
        var setSeries = Str(set, "series");
        var releaseDate = Str(set, "releaseDate");
        var printedTotal = Int(set, "printedTotal") ?? Int(set, "total");

        foreach (var card in cards.EnumerateArray())
        {
            if (Str(card, "id") is not { } id) continue;

            p["$id"].Value = id;
            p["$name"].Value = Str(card, "name") ?? id;
            p["$setId"].Value = setId;
            p["$setName"].Value = (object?)setName ?? DBNull.Value;
            p["$setSeries"].Value = (object?)setSeries ?? DBNull.Value;
            p["$number"].Value = (object?)Str(card, "number") ?? DBNull.Value;
            p["$printedTotal"].Value = (object?)printedTotal ?? DBNull.Value;
            p["$rarity"].Value = (object?)Str(card, "rarity") ?? DBNull.Value;
            p["$supertype"].Value = (object?)Str(card, "supertype") ?? DBNull.Value;
            p["$subtypes"].Value = (object?)Join(card, "subtypes") ?? DBNull.Value;
            p["$types"].Value = (object?)Join(card, "types") ?? DBNull.Value;
            p["$artist"].Value = (object?)Str(card, "artist") ?? DBNull.Value;
            p["$releaseDate"].Value = (object?)releaseDate ?? DBNull.Value;
            p["$imageUrl"].Value = (object?)ImageUrl(card) ?? DBNull.Value;

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Fetches the artwork for everything not already held, re-encoding each one to
    /// WebP on the way in. Resumable by construction: it only ever looks at rows
    /// whose image is still missing, so a cancelled or failed run picks up where it
    /// stopped rather than starting again.
    /// </summary>
    private async Task DownloadImagesAsync(CancellationToken ct)
    {
        var pending = PendingImages();
        _progress = new CatalogueProgress("images", 0, pending.Count, "Downloading artwork", null);
        if (pending.Count == 0) return;

        Directory.CreateDirectory(paths.CatalogueImagesDirectory);

        using var http = CreateClient();
        using var gate = new SemaphoreSlim(ImageConcurrency);
        var done = 0;
        var failed = 0;

        var work = pending.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await StoreImageAsync(http, item.Id, item.Url, ct)) MarkImageStored(item.Id);
                else Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
                var count = Interlocked.Increment(ref done);
                // Updating a record per image is cheap; updating the UI per image is
                // not, so the progress object is only replaced every so often.
                if (count % 25 == 0 || count == pending.Count)
                    _progress = _progress! with { Done = count };
            }
        });

        await Task.WhenAll(work);

        _progress = _progress! with
        {
            Done = pending.Count,
            Detail = failed > 0 ? $"{failed} image(s) unavailable" : null,
        };

        if (failed > 0) log.LogWarning("{Failed} catalogue image(s) could not be fetched", failed);
    }

    /// <summary>
    /// Downloads one image and writes it as WebP. Returns false when the artwork
    /// simply isn't there, which is normal enough not to be an error — a handful of
    /// cards have a URL that 404s.
    /// </summary>
    private async Task<bool> StoreImageAsync(HttpClient http, string id, string url, CancellationToken ct)
    {
        var file = ImagePath(id);
        if (File.Exists(file)) return true;

        try
        {
            var bytes = await http.GetByteArrayAsync(url, ct);

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
            {
                log.LogWarning("Could not decode catalogue image for {CardId}", id);
                return false;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            if (encoded is null) return false;

            // Written aside and moved, so a cancelled run can't leave a half-written
            // file that later looks like a complete one.
            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, encoded.ToArray(), ct);
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Could not store catalogue image for {CardId}", id);
            return false;
        }
    }

    // ---------------------------------------------------------------------- reading

    /// <summary>
    /// Cards matching what was typed, straight from SQLite. Ordered by set date so
    /// the most recent printing of a card comes first, which is usually the one in
    /// your hand.
    /// </summary>
    public List<CatalogueCard> Search(SearchIntent intent, int limit)
    {
        if (SearchQuery.ToSql(intent) is not var (where, parameters)) return [];

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, set_id, set_name, set_series, number, printed_total,
                   rarity, supertype, types, artist, release_date, has_image
            FROM catalogue
            WHERE {where}
            ORDER BY release_date DESC, name
            LIMIT $limit
            """;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 250));

        var results = new List<CatalogueCard>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(Map(r));
        return results;
    }

    private static CatalogueCard Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetString(0),
        Name: r.GetString(1),
        SetId: r.IsDBNull(2) ? null : r.GetString(2),
        SetName: r.IsDBNull(3) ? null : r.GetString(3),
        SetSeries: r.IsDBNull(4) ? null : r.GetString(4),
        Number: r.IsDBNull(5) ? null : r.GetString(5),
        PrintedTotal: r.IsDBNull(6) ? null : r.GetInt32(6),
        Rarity: r.IsDBNull(7) ? null : r.GetString(7),
        Supertype: r.IsDBNull(8) ? null : r.GetString(8),
        Types: r.IsDBNull(9) ? [] : r.GetString(9).Split(',', StringSplitOptions.RemoveEmptyEntries),
        Artist: r.IsDBNull(10) ? null : r.GetString(10),
        ReleaseDate: r.IsDBNull(11) ? null : r.GetString(11),
        HasImage: !r.IsDBNull(12) && r.GetInt32(12) == 1);

    /// <summary>
    /// One card by id. Used when adding, where the row has to become a collection
    /// entry rather than a search result.
    /// </summary>
    public CatalogueCard? Get(string cardId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, set_id, set_name, set_series, number, printed_total,
                   rarity, supertype, types, artist, release_date, has_image
            FROM catalogue WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", cardId);

        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>
    /// Catalogue rows matching any combination of set, number and name — the same
    /// question <see cref="CardCache.FindPayloads"/> asks of the cards we hold real
    /// payloads for, asked instead of the twenty thousand we merely know about.
    ///
    /// The printed total is applied in SQL rather than left to the caller's narrowing,
    /// and that is entirely down to the row limit: "4" on its own matches card 4 in
    /// very nearly every set ever printed, so trimming to a page of results before the
    /// denominator is considered can leave the right card off the page altogether.
    /// When the denominator matches nothing the query is repeated without it, so a
    /// wrong or unknown total widens the search rather than emptying it.
    /// </summary>
    public List<CatalogueCard> FindCards(
        string? setId, string? number, string? name, int? printedTotal, int limit = 50)
    {
        var rows = FindCardsCore(setId, number, name, printedTotal, limit);
        if (rows.Count == 0 && printedTotal is not null)
            rows = FindCardsCore(setId, number, name, null, limit);
        return rows;
    }

    private List<CatalogueCard> FindCardsCore(
        string? setId, string? number, string? name, int? printedTotal, int limit)
    {
        var clauses = new List<string>();
        var pars = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(setId))
        {
            clauses.Add("LOWER(set_id) = $setId");
            pars["$setId"] = setId.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(number))
        {
            clauses.Add("LOWER(number) = $number");
            pars["$number"] = number.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            clauses.Add("LOWER(name) LIKE $name");
            pars["$name"] = $"%{name.Trim().ToLowerInvariant()}%";
        }

        if (printedTotal is { } total)
        {
            clauses.Add("printed_total = $printedTotal");
            pars["$printedTotal"] = total;
        }

        // Never answer an unconstrained question with the whole catalogue.
        if (clauses.Count == 0) return [];

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, set_id, set_name, set_series, number, printed_total,
                   rarity, supertype, types, artist, release_date, has_image
            FROM catalogue
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY release_date DESC, name
            LIMIT $limit
            """;
        foreach (var (key, value) in pars) cmd.Parameters.AddWithValue(key, value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 250));

        var results = new List<CatalogueCard>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(Map(r));
        return results;
    }

    /// <summary>
    /// An API-shaped payload for a catalogue card, so it can enter the collection
    /// without the network being involved at all.
    ///
    /// Carries no price block, which is what keeps the promise: a card added this way
    /// shows no price rather than a made-up one, until the daily refresh fetches the
    /// real thing. The printing list is likewise absent here and guessed at the point
    /// of display, since that's a presentation decision rather than something to bake
    /// into stored data.
    /// </summary>
    public JsonElement? BuildPayload(string cardId)
    {
        if (Get(cardId) is not { } card) return null;

        var imageSmall = ImageUrlFor(cardId);
        var payload = JsonSerializer.SerializeToElement(new
        {
            id = card.Id,
            name = card.Name,
            number = card.Number,
            rarity = card.Rarity,
            supertype = card.Supertype,
            types = card.Types,
            artist = card.Artist,
            set = new
            {
                id = card.SetId,
                name = card.SetName,
                series = card.SetSeries,
                releaseDate = card.ReleaseDate,
                printedTotal = card.PrintedTotal,
            },
            images = new { small = imageSmall, large = LargeUrl(imageSmall) },
        });

        return payload;
    }

    private string? ImageUrlFor(string cardId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT image_url FROM catalogue WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", cardId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// The detail-view artwork, derived from the thumbnail URL.
    ///
    /// The catalogue only stores the small one — the large scans are the difference
    /// between a 3 GB download and a 19 GB one. Both hosts name the pair predictably,
    /// so opening a single card can still fetch its full-size art on demand.
    /// </summary>
    private static string? LargeUrl(string? small)
    {
        if (string.IsNullOrWhiteSpace(small)) return null;

        // images.scrydex.com/pokemon/me5-1/small -> .../large
        if (small.EndsWith("/small", StringComparison.OrdinalIgnoreCase))
            return small[..^"/small".Length] + "/large";

        // images.pokemontcg.io/base1/4.png -> .../4_hires.png
        var dot = small.LastIndexOf('.');
        return dot > 0 ? $"{small[..dot]}_hires{small[dot..]}" : small;
    }

    /// <summary>
    /// Where a catalogue card's artwork lives upstream, for cards we know about but
    /// hold no payload for.
    ///
    /// Import review is the case that needs this: rows resolved from the catalogue are
    /// not in <c>cards</c> until they're committed, so the on-demand image cache has
    /// nowhere to look and the review list draws empty boxes — in the one screen whose
    /// entire job is letting you check a card against its picture. Downloading the
    /// catalogue's own artwork avoids the fetch altogether; this covers the case where
    /// it was skipped.
    /// </summary>
    public string? RemoteImageUrl(string cardId, string size)
    {
        var small = ImageUrlFor(cardId);
        return size == "large" ? LargeUrl(small) : small;
    }

    /// <summary>The stored artwork for a card, or null if it wasn't downloaded.</summary>
    public string? ImageFile(string cardId)
    {
        var file = ImagePath(cardId);
        return File.Exists(file) ? file : null;
    }

    // --------------------------------------------------------------------- deleting

    /// <summary>
    /// Removes the catalogue entirely — rows, artwork and the record of when it was
    /// taken. Nothing else references it, so this can't affect the cards you own or
    /// their cached images, which live in a different table and a different folder.
    /// </summary>
    public void Delete()
    {
        Cancel();

        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM catalogue";
            cmd.ExecuteNonQuery();
        }

        try
        {
            if (Directory.Exists(paths.CatalogueImagesDirectory))
                Directory.Delete(paths.CatalogueImagesDirectory, recursive: true);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not remove the catalogue image folder");
        }

        settings.Delete(DownloadedAtSetting);
        _progress = null;
        log.LogInformation("Offline catalogue deleted");
    }

    // ---------------------------------------------------------------------- plumbing

    private string ImagePath(string cardId)
    {
        var safe = string.Concat(cardId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return Path.Combine(paths.CatalogueImagesDirectory, $"{safe}.webp");
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        // GitHub's raw host and the image CDNs both want a user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CardVault");
        return client;
    }

    private int CardCount()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalogue";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private (int Count, long Bytes) ImageStats()
    {
        if (!Directory.Exists(paths.CatalogueImagesDirectory)) return (0, 0);

        var files = new DirectoryInfo(paths.CatalogueImagesDirectory).EnumerateFiles("*.webp").ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    private List<(string Id, string Url)> PendingImages()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, image_url FROM catalogue
            WHERE has_image = 0 AND image_url IS NOT NULL AND image_url <> ''
            """;

        var pending = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) pending.Add((r.GetString(0), r.GetString(1)));
        return pending;
    }

    private void MarkImageStored(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE catalogue SET has_image = 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static string? ImageUrl(JsonElement card)
        => card.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object
            ? Str(images, "small")
            : null;

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static string? Join(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(",", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
            : null;
}
