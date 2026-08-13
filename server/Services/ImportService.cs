using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using PokemonVault.Models;

namespace PokemonVault.Services;

/// <summary>
/// Turns a CSV export into reviewable collection entries.
///
/// Resolution runs as a background job because a few hundred rows can mean a few
/// hundred API round-trips, which is far longer than a browser will hold a request
/// open. The UI starts a job, polls progress, lets you fix up anything ambiguous,
/// then commits — nothing is written to the collection until you say so.
/// </summary>
public sealed class ImportService(
    CardCache cache,
    PokemonTcgClient api,
    CollectionService collection,
    PriceSnapshotService snapshots,
    IHostApplicationLifetime lifetime,
    ILogger<ImportService> log)
{
    private readonly ConcurrentDictionary<string, ImportJob> _jobs = new();

    /// <summary>Header aliases, so exports from different tools work unmodified.</summary>
    private static readonly Dictionary<string, string[]> ColumnAliases = new()
    {
        ["cardId"] = ["cardid", "card id", "id", "pokemontcg id", "pokemontcgid", "api id"],
        ["name"] = ["name", "card name", "cardname", "card", "product name", "title"],
        ["setId"] = ["setid", "set id", "set code id"],
        ["setCode"] = ["setcode", "set code", "ptcgo code", "ptcgocode", "set abbreviation", "abbr"],
        ["setName"] = ["set", "set name", "setname", "edition", "expansion"],
        ["number"] = ["number", "card number", "cardnumber", "collector number", "collectornumber", "no", "#"],
        ["quantity"] = ["quantity", "qty", "count", "amount", "copies"],
        ["variant"] = ["variant", "printing", "finish", "foil", "edition type", "parallel"],
        ["condition"] = ["condition", "cond", "grade condition"],
        ["grade"] = ["grade", "graded", "cert", "certification"],
        ["purchasePrice"] = ["purchase price", "purchaseprice", "paid", "price paid", "cost", "buy price", "my price"],
        ["purchaseDate"] = ["purchase date", "purchasedate", "date acquired", "acquired", "date added", "date"],
        ["notes"] = ["notes", "note", "comment", "comments", "description"],
        ["location"] = ["location", "storage", "binder", "box", "where", "stored", "placement"],
    };

    public ImportJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public ImportJob Start(string csv)
    {
        var rows = Csv.Parse(csv);
        var job = new ImportJob { Id = Guid.NewGuid().ToString("n")[..12] };

        if (rows.Count < 2)
        {
            job.State = "failed";
            job.Error = "That file needs a header row and at least one card.";
            _jobs[job.Id] = job;
            return job;
        }

        var (map, unmapped) = MapColumns(rows[0]);
        job.UnmappedColumns = unmapped;

        if (map.Count == 0)
        {
            job.State = "failed";
            job.Error =
                "None of the columns were recognised. At minimum include a card name, " +
                "or a set and card number.";
            _jobs[job.Id] = job;
            return job;
        }

        var dataRows = rows.Skip(1).ToList();
        job.Total = dataRows.Count;
        _jobs[job.Id] = job;

        PruneOldJobs();

        // Fire and forget: the client polls GET /api/import/{id} for progress.
        // Deliberately NOT the request's token — that's cancelled the moment this
        // POST responds, which would kill the job before it resolved a single row.
        // Application shutdown is the only thing that should stop it.
        _ = Task.Run(() => ResolveAllAsync(job, dataRows, map, lifetime.ApplicationStopping),
            CancellationToken.None);

        return job;
    }

    private async Task ResolveAllAsync(ImportJob job, List<string[]> dataRows, Dictionary<string, int> map, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < dataRows.Count; i++)
            {
                var row = BuildRow(i, dataRows[i], map);
                await ResolveAsync(row, ct);
                job.Rows.Add(row);
                job.Processed = i + 1;
            }

            job.State = "ready";
        }
        catch (OperationCanceledException)
        {
            job.State = "failed";
            job.Error = "The server shut down mid-import. Rows resolved so far are still shown.";
        }
        catch (Exception e)
        {
            log.LogError(e, "Import job {JobId} failed", job.Id);
            job.State = "failed";
            job.Error = "The import stopped unexpectedly. Any rows resolved so far are still shown.";
        }
    }

    // ------------------------------------------------------------------ mapping

    private static (Dictionary<string, int> Map, List<string> Unmapped) MapColumns(string[] header)
    {
        var map = new Dictionary<string, int>();
        var unmapped = new List<string>();

        for (var i = 0; i < header.Length; i++)
        {
            var raw = header[i].Trim();
            var key = raw.ToLowerInvariant().Replace("_", " ").Replace("-", " ").Trim();

            var field = ColumnAliases.FirstOrDefault(kv => kv.Value.Contains(key)).Key;
            if (field is not null) map.TryAdd(field, i);
            else if (raw.Length > 0) unmapped.Add(raw);
        }

        return (map, unmapped);
    }

    private static ImportRow BuildRow(int index, string[] cells, Dictionary<string, int> map)
    {
        string? Get(string field)
        {
            if (!map.TryGetValue(field, out var col) || col >= cells.Length) return null;
            var v = cells[col].Trim();
            return v.Length == 0 ? null : v;
        }

        var row = new ImportRow
        {
            Index = index,
            Source = string.Join(", ", cells.Where(c => c.Trim().Length > 0)),
            CardId = Get("cardId"),
            Name = Get("name"),
            SetName = Get("setName"),
            SetId = Get("setId"),
            SetCode = Get("setCode"),
            Number = Get("number"),
            Quantity = ParseQuantity(Get("quantity")),
            Condition = NormalizeCondition(Get("condition")),
            Grade = Get("grade"),
            PurchasePrice = ParseMoney(Get("purchasePrice")),
            PurchaseDate = ParseDate(Get("purchaseDate")),
            Notes = Get("notes"),
            Location = Get("location"),
        };

        // Held separately from the resolved card's own values until we match it.
        row.Variant = NormalizeVariant(Get("variant"));
        return row;
    }

    // --------------------------------------------------------------- resolution

    private async Task ResolveAsync(ImportRow row, CancellationToken ct)
    {
        var setId = ExtractSetId(row);
        var setCode = row.SetCode;

        // 1. An explicit card id is unambiguous — take it straight.
        if (!string.IsNullOrWhiteSpace(row.CardId))
        {
            var payload = cache.GetPayload(row.CardId);
            if (payload is null)
            {
                var fetched = await FetchCardAsync(row.CardId, ct);
                if (fetched is not null)
                {
                    cache.Upsert(fetched.Value);
                    payload = fetched.Value.GetRawText();
                }
            }

            if (payload is not null)
            {
                Apply(row, ToCandidate(payload));
                return;
            }

            row.Status = ImportStatus.NotFound;
            row.Message = $"No card with id '{row.CardId}'.";
            return;
        }

        if (row.Name is null && row.Number is null)
        {
            row.Status = ImportStatus.Invalid;
            row.Message = "Row has no card name, id or number to look up.";
            return;
        }

        // 2. Try the local cache before touching the network.
        var local = cache.FindPayloads(setId, row.Number, setId is null ? row.Name : null);
        if (local.Count > 0)
        {
            var candidates = local.Select(ToCandidate).ToList();
            var narrowed = NarrowBySetName(NarrowByName(candidates, row.Name), row.SetName);
            if (narrowed.Count == 1)
            {
                Apply(row, narrowed[0]);
                return;
            }
        }

        // 3. Ask the API, most precise query form first.
        var queries = new List<string>();
        if (setId is not null && row.Number is not null) queries.Add($"set.id:{Escape(setId)} number:{Escape(row.Number)}");
        if (setCode is not null && row.Number is not null) queries.Add($"set.ptcgoCode:{Escape(setCode)} number:{Escape(row.Number)}");
        if (row.SetName is not null && row.Number is not null) queries.Add($"set.name:\"{Escape(row.SetName)}\" number:{Escape(row.Number)}");
        if (row.Name is not null && row.Number is not null) queries.Add($"name:\"*{Escape(row.Name)}*\" number:{Escape(row.Number)}");
        if (row.Name is not null && row.SetName is not null) queries.Add($"name:\"*{Escape(row.Name)}*\" set.name:\"{Escape(row.SetName)}\"");
        if (row.Name is not null) queries.Add($"name:\"*{Escape(row.Name)}*\"");

        var lookupFailed = false;

        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();

            List<string> payloads;
            try
            {
                var res = await api.SearchCardsAsync(query, 1, 12, ct);
                if (!res.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;

                var cards = data.EnumerateArray().Select(c => c.Clone()).ToList();
                if (cards.Count == 0) continue;

                cache.UpsertMany(cards);
                payloads = cards.Select(c => c.GetRawText()).ToList();
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Import lookup failed for query {Query}", query);
                lookupFailed = true;
                continue;
            }
            finally
            {
                // Pace ourselves so a long file doesn't trip the rate limit.
                await Task.Delay(150, ct);
            }

            var candidates = payloads.Select(ToCandidate).ToList();
            var narrowed = NarrowBySetName(NarrowByName(candidates, row.Name), row.SetName);

            if (narrowed.Count == 1)
            {
                Apply(row, narrowed[0]);
                return;
            }

            if (narrowed.Count > 1)
            {
                row.Status = ImportStatus.Ambiguous;
                row.Candidates = narrowed.Take(12).ToList();
                row.Message = $"{narrowed.Count} cards match — pick the right printing.";
                return;
            }
        }

        if (lookupFailed)
        {
            row.Status = ImportStatus.LookupFailed;
            row.Message = "Couldn't reach the catalogue for this row — re-run the import to retry it.";
            return;
        }

        row.Status = ImportStatus.NotFound;
        row.Message = "No card in the catalogue matched this row.";
    }

    private async Task<JsonElement?> FetchCardAsync(string id, CancellationToken ct)
    {
        try
        {
            var card = await api.GetCardAsync(id, ct);
            await Task.Delay(150, ct);
            return card;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Import could not fetch card {CardId}", id);
            return null;
        }
    }

    /// <summary>
    /// The API's name matching is fuzzy — searching "Charizard" also returns
    /// "Blaine's Charizard" and "Dark Charizard". When the CSV gave us a name,
    /// prefer exact matches so those near-misses don't make every row ambiguous.
    /// </summary>
    private static List<CardCandidate> NarrowByName(List<CardCandidate> candidates, string? name)
    {
        if (candidates.Count <= 1 || string.IsNullOrWhiteSpace(name)) return candidates;

        var wanted = name.Trim();
        var exact = candidates
            .Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return exact.Count > 0 ? exact : candidates;
    }

    /// <summary>
    /// Set names match loosely too — "Base" pulls in "Base Set 2". If the CSV named
    /// a set exactly, trust it rather than making the user disambiguate.
    /// </summary>
    private static List<CardCandidate> NarrowBySetName(List<CardCandidate> candidates, string? setName)
    {
        if (candidates.Count <= 1 || string.IsNullOrWhiteSpace(setName)) return candidates;

        var wanted = setName.Trim();
        var exact = candidates
            .Where(c => string.Equals(c.SetName, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return exact.Count > 0 ? exact : candidates;
    }

    private static void Apply(ImportRow row, CardCandidate card)
    {
        row.Status = ImportStatus.Matched;
        row.CardId = card.CardId;
        row.Name = card.Name;
        row.SetName = card.SetName;
        row.Number = card.Number;
        row.Rarity = card.Rarity;
        row.ImageSmall = card.ImageSmall;
        row.MarketPrice = card.MarketPrice;
        row.Variants = card.Variants;

        // The CSV's printing may not exist for this card (a "reverse holo" that was
        // never printed as one). Fall back rather than storing a variant with no price.
        if (card.Variants.Count > 0 && !card.Variants.Contains(row.Variant))
        {
            var fallback = card.Variants[0];
            row.Message = $"No '{row.Variant}' printing — using '{fallback}'.";
            row.Variant = fallback;
        }
    }

    private static CardCandidate ToCandidate(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var card = doc.RootElement;
        var set = card.TryGetProperty("set", out var s) ? s : default;
        var images = card.TryGetProperty("images", out var im) ? im : default;
        var price = Pricing.ForVariant(card, null);

        return new CardCandidate(
            CardId: Text(card, "id") ?? "",
            Name: Text(card, "name") ?? "",
            SetName: Text(set, "name"),
            Number: Text(card, "number"),
            Rarity: Text(card, "rarity"),
            ImageSmall: Text(images, "small"),
            MarketPrice: price.Market,
            Variants: Pricing.AvailableVariants(card));

        static string? Text(JsonElement el, string prop)
            => el.ValueKind == JsonValueKind.Object
               && el.TryGetProperty(prop, out var v)
               && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? v.ToString()
                : null;
    }

    // -------------------------------------------------------------------- commit

    public int Commit(string jobId, IReadOnlyList<CommitRow> rows)
    {
        var added = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CardId) || !cache.Has(row.CardId)) continue;

            collection.Add(new AddEntryRequest(
                CardId: row.CardId,
                Quantity: row.Quantity,
                Variant: row.Variant,
                Condition: row.Condition,
                Grade: row.Grade,
                PurchasePrice: row.PurchasePrice,
                PurchaseDate: row.PurchaseDate,
                Notes: row.Notes,
                Location: row.Location));

            // Same as a single add: give each imported card a starting price point.
            snapshots.RecordCurrentPrices(row.CardId);
            added++;
        }

        if (_jobs.TryGetValue(jobId, out var job)) job.State = "committed";
        return added;
    }

    // ------------------------------------------------------------ normalisation

    /// <summary>Prefers an explicit set id column, else derives one from a "base1-4" style card id.</summary>
    private static string? ExtractSetId(ImportRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.SetId)) return row.SetId.Trim();
        if (row.CardId is { } id && id.Contains('-')) return id[..id.LastIndexOf('-')];
        return null;
    }

    private static string Escape(string value) => value.Replace("\"", "").Replace("\\", "").Trim();

    private static int ParseQuantity(string? raw)
        => int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) && q > 0 ? q : 1;

    private static double? ParseMoney(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ParseDate(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd")
            : null;

    private static string NormalizeCondition(string? raw)
    {
        var c = raw?.Trim().ToLowerInvariant() ?? "";
        if (c.Length == 0) return "NM";
        if (c.StartsWith("nm") || c.Contains("near mint") || c is "m" or "mint") return "NM";
        if (c.StartsWith("lp") || c.Contains("lightly") || c.Contains("excellent") || c.StartsWith("sp") || c.Contains("slightly")) return "LP";
        if (c.StartsWith("mp") || c.Contains("moderately") || c is "good" or "gd" || c == "played") return "MP";
        if (c.StartsWith("hp") || c.Contains("heavily") || c.Contains("poor")) return "HP";
        if (c.StartsWith("d") || c.Contains("damaged")) return "DMG";
        return "NM";
    }

    private static string NormalizeVariant(string? raw)
    {
        var v = raw?.Trim().ToLowerInvariant() ?? "";
        if (v.Length == 0) return "normal";

        var firstEdition = v.Contains("1st") || v.Contains("first");
        var reverse = v.Contains("reverse");
        var holo = v.Contains("holo") || v.Contains("foil");

        if (reverse) return "reverseHolofoil";
        if (firstEdition) return holo ? "1stEditionHolofoil" : "1stEditionNormal";
        if (holo) return "holofoil";
        return "normal";
    }

    /// <summary>Keeps finished jobs around briefly for polling, then lets them go.</summary>
    private void PruneOldJobs()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var (id, job) in _jobs)
            if (job.CreatedAt < cutoff)
                _jobs.TryRemove(id, out _);
    }
}
