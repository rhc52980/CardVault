using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CardVault.Models;

namespace CardVault.Services;

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
    CatalogueService catalogue,
    PokemonTcgClient api,
    CollectionService collection,
    PriceSnapshotService snapshots,
    ImportBatchService batches,
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
        ["language"] = ["language", "lang", "printing language", "locale"],
    };

    /// <summary>
    /// A job, but only to the collection it belongs to. Jobs are held in one place
    /// for the whole process, and another vault has no business reading this one.
    /// </summary>
    public ImportJob? Get(string id)
        => _jobs.GetValueOrDefault(id) is { } job && job.Vault == Data.CurrentVault.Id ? job : null;

    public ImportJob Start(string csv)
    {
        var rows = Csv.Parse(csv);
        var job = new ImportJob
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Vault = Data.CurrentVault.Id,
        };

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
            Language = Languages.Normalize(Get("language")),
            PurchasePrice = ParseMoney(Get("purchasePrice")),
            PurchaseDate = ParseDate(Get("purchaseDate")),
            Notes = Get("notes"),
            Location = Get("location"),
        };

        // A number column written the way the card prints it — "45/094", or any of
        // the forms the search box takes. Parsed through the very same code, so a
        // spreadsheet and the search box can't drift apart on what a number means.
        //
        // Without this the cell arrives as the literal text "45/094", which matches
        // no card at all; and stripped to just "45" it would match card 45 in
        // practically every set ever printed. The denominator is what makes a
        // column of bare numbers usable.
        if (row.Number is { } typed)
        {
            var intent = SearchQuery.Parse(typed, null);
            if (intent.Number is { } parsed)
            {
                row.Number = parsed;
                row.PrintedTotal = intent.PrintedTotal;
            }
        }

        // Held separately from the resolved card's own values until we match it.
        row.Variant = NormalizeVariant(Get("variant"));

        // Snapshot of what the CSV claimed, taken before anything resolves and starts
        // overwriting the row with a matched card's details.
        row.ClaimedName = row.Name;
        row.ClaimedNumber = row.Number;
        return row;
    }

    // --------------------------------------------------------------- resolution

    private async Task ResolveAsync(ImportRow row, CancellationToken ct)
    {
        var setId = ExtractSetId(row);
        var setCode = row.SetCode;

        // 1. An explicit card id resolves to exactly one card — but see below, that is
        //    not the same thing as it being the right one.
        if (!string.IsNullOrWhiteSpace(row.CardId))
        {
            var byId = await ResolveIdAsync(row.CardId, ct);
            if (byId is null)
            {
                row.Status = ImportStatus.NotFound;
                row.Message = $"No card with id '{row.CardId}'.";
                return;
            }

            // The id resolved, so we could stop here — and until the row is checked
            // against what was transcribed off the same card, stopping here is exactly
            // how a misread set symbol becomes a confidently imported wrong card.
            var conflict = CardIdCheck.Contradiction(
                byId.CardId, byId.Name, byId.Number, row.ClaimedName, row.ClaimedNumber);

            if (conflict is not null)
            {
                var alternatives = FindLocalCandidates(setId, row.ClaimedNumber, row.ClaimedName, row.PrintedTotal)
                    .Where(c => c.CardId != byId.CardId)
                    .ToList();

                row.Status = ImportStatus.Mismatch;
                row.Message = alternatives.Count > 0
                    ? $"Check this one — {conflict}."
                    : $"Check this one — {conflict}. Nothing else matched the row, so either the id is right and the rest of the row is wrong, or this card needs re-scanning.";

                // The id's card first: it is still the most likely answer, and leading
                // with it means the thumbnail is on screen to be compared against.
                row.Candidates = new List<CardCandidate> { byId }
                    .Concat(alternatives)
                    .Take(12)
                    .ToList();

                // Carries the id's card so its art renders, but Matched is deliberately
                // withheld — this row does not get to be ticked without being looked at.
                Describe(row, byId);
                return;
            }

            Apply(row, byId);
            return;
        }

        if (row.Name is null && row.Number is null)
        {
            row.Status = ImportStatus.Invalid;
            row.Message = "Row has no card name, id or number to look up.";
            return;
        }

        // 2. Try the local cache before touching the network.
        //
        // Only when the row says something about which card it means beyond a bare
        // number. The cache holds whatever happens to have been seen before, so one
        // hit in it does not mean one card exists: a row reading only "45" would
        // match whichever card numbered 45 had been cached first and import it
        // silently, which is a far worse outcome than asking the API. A number
        // written "45/094" is specific enough, because the denominator names the
        // set as surely as spelling it out.
        var identifiable = setId is not null
                           || setCode is not null
                           || row.SetName is not null
                           || row.Name is not null
                           || row.PrintedTotal is not null;

        if (identifiable)
        {
            // The cards we already hold payloads for come first: they carry real prices
            // and a real printing list, neither of which the catalogue can offer.
            // The name is left out once a number and a denominator are both present:
            // between them they identify the card, and a misread name would hide it.
            // Narrow still prefers an exact name match among whatever comes back.
            var byNumberAlone = row.Number is not null && row.PrintedTotal is not null;
            var cacheName = setId is null && !byNumberAlone ? row.Name : null;

            var cached = Narrow(
                cache.FindPayloads(setId, row.Number, cacheName).Select(ToCandidate).ToList(), row);

            if (cached.Count == 1)
            {
                Apply(row, cached[0]);
                return;
            }

            // 3. Then the offline catalogue. Unlike the cache it knows about every card
            //    rather than the ones that happen to have been seen before, and that
            //    completeness is why ambiguity here is worth presenting as a choice
            //    instead of falling through to the network: asking pokemontcg.io the
            //    same question cannot narrow it any further, and would trade ten
            //    seconds and a coin-flip on a 500 for the identical answer.
            //
            //    Coming up empty still falls through, because a catalogue downloaded
            //    months ago has never heard of last month's set.
            if (catalogue.IsUsable)
            {
                var found = catalogue.FindCards(setId, row.Number, cacheName, row.PrintedTotal)
                                     .Select(ToCandidate).ToList();
                var offline = Narrow(found, row);

                if (offline.Count == 1)
                {
                    Apply(row, offline[0], found);
                    return;
                }

                if (offline.Count > 1)
                {
                    row.Status = ImportStatus.Ambiguous;
                    row.Candidates = offline.Take(12).ToList();
                    row.Message = $"{offline.Count} cards match — pick the right printing.";
                    return;
                }
            }
        }

        // 4. Ask the API, most precise query form first.
        var queries = new List<string>();
        if (setId is not null && row.Number is not null) queries.Add($"set.id:{Escape(setId)} number:{Escape(row.Number)}");
        if (setCode is not null && row.Number is not null) queries.Add($"set.ptcgoCode:{Escape(setCode)} number:{Escape(row.Number)}");

        // The denominator narrows to the handful of sets of that exact size, which
        // for a bare "45/094" is the difference between one card and one per set.
        // Placed above the name forms because it is more precise than either.
        if (row.PrintedTotal is { } printedTotal && row.Number is not null)
        {
            // Without the name, deliberately, and before the form that includes it.
            // A number and a denominator identify a card between them; adding a name
            // that OCR got slightly wrong turns a good query into no results and a
            // wasted round trip against an API that is slow and often fails. Whatever
            // comes back is narrowed by name afterwards, which prefers an exact match
            // and keeps everything when there isn't one — so a correct name still
            // decides, and a wrong one no longer excludes.
            queries.Add($"number:{Escape(row.Number)} set.printedTotal:{printedTotal}");

            if (row.Name is not null)
                queries.Add($"name:\"*{Escape(row.Name)}*\" number:{Escape(row.Number)} set.printedTotal:{printedTotal}");
        }

        if (row.SetName is not null && row.Number is not null) queries.Add($"set.name:\"{Escape(row.SetName)}\" number:{Escape(row.Number)}");
        if (row.Name is not null && row.Number is not null) queries.Add($"name:\"*{Escape(row.Name)}*\" number:{Escape(row.Number)}");
        if (row.Name is not null && row.SetName is not null) queries.Add($"name:\"*{Escape(row.Name)}*\" set.name:\"{Escape(row.SetName)}\"");
        if (row.Name is not null) queries.Add($"name:\"*{Escape(row.Name)}*\"");

        // Last resort: a number and nothing else. It will match that number in many
        // sets, which is the point — the row comes back as a choice to make rather
        // than as "not found", which would be untrue since nothing was ever asked.
        if (row.Name is null && row.Number is not null) queries.Add($"number:{Escape(row.Number)}");

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

            var all = payloads.Select(ToCandidate).ToList();
            var narrowed = Narrow(all, row);

            if (narrowed.Count == 1)
            {
                Apply(row, narrowed[0], all);
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

    /// <summary>
    /// One card by id, from whatever can answer soonest: the payloads we hold, then
    /// the offline catalogue, then the network. A CSV whose Card ID column is filled
    /// in resolves entirely offline when the catalogue is present.
    /// </summary>
    private async Task<CardCandidate?> ResolveIdAsync(string cardId, CancellationToken ct)
    {
        if (cache.GetPayload(cardId) is { } payload) return ToCandidate(payload);

        if (catalogue.IsUsable && catalogue.Get(cardId) is { } offline) return ToCandidate(offline);

        var fetched = await FetchCardAsync(cardId, ct);
        if (fetched is null) return null;

        cache.Upsert(fetched.Value);
        return ToCandidate(fetched.Value.GetRawText());
    }

    /// <summary>
    /// Everything locally known that fits the row, cache and catalogue together, with
    /// no network involved. Used to offer alternatives beside a card id the rest of the
    /// row disagrees with, where going to the API would mean a request per doubted row.
    /// </summary>
    private List<CardCandidate> FindLocalCandidates(string? setId, string? number, string? name, int? printedTotal)
    {
        if (number is null && name is null) return [];

        var found = cache.FindPayloads(setId, number, name).Select(ToCandidate).ToList();

        if (catalogue.IsUsable)
        {
            var known = found.Select(c => c.CardId).ToHashSet();
            found.AddRange(catalogue.FindCards(setId, number, name, printedTotal)
                                    .Select(ToCandidate)
                                    .Where(c => known.Add(c.CardId)));
        }

        return found;
    }

    /// <summary>
    /// The other printings a matched row could have been, chosen card first.
    ///
    /// Empty when there was no genuine choice: one candidate is not an alternative,
    /// and a picker offering a single option would be noise on every row of a
    /// hundred-row import. Capped, because a bare number with no denominator can
    /// match a great many cards and a list that long is not a choice either.
    /// </summary>
    internal static List<CardCandidate> Alternatives(CardCandidate chosen, List<CardCandidate>? found)
        => found is { Count: > 1 }
            ? [.. found.OrderByDescending(c => c.CardId == chosen.CardId).Take(12)]
            : [];

    private static List<CardCandidate> Narrow(List<CardCandidate> candidates, ImportRow row)
        => NarrowBySetName(
            NarrowByName(NarrowByPrintedTotal(candidates, row.PrintedTotal), row.Name),
            row.SetName);

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
    /// <summary>
    /// Keeps only cards from a set of exactly that size.
    ///
    /// This is the whole value of writing a number as "45/094": card 45 exists in
    /// very nearly every set ever printed, and the denominator cuts that to the few
    /// sets that are 94 cards long. Applied before name and set-name narrowing
    /// because it is the strongest signal of the three and needs nothing else in
    /// the row to work.
    /// </summary>
    private static List<CardCandidate> NarrowByPrintedTotal(List<CardCandidate> candidates, int? total)
    {
        if (candidates.Count <= 1 || total is null) return candidates;

        var matching = candidates.Where(c => c.PrintedTotal == total).ToList();

        // If nothing matches, the denominator was wrong or the set isn't known —
        // better to fall back to the wider list than to reject the row outright.
        return matching.Count > 0 ? matching : candidates;
    }

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

    /// <summary>
    /// Settles a row on one card.
    ///
    /// <paramref name="alsoMatched"/> is what the search found before narrowing chose
    /// between them. Kept rather than discarded so the review can offer them: a number
    /// like 153/189 belongs to two different sets, and narrowing picks one on evidence
    /// that may be thin — the CSV naming a set it guessed at. Being told "matched, and
    /// here are the others it could be" is the difference between a decision you made
    /// and one made for you.
    /// </summary>
    internal static void Apply(ImportRow row, CardCandidate card, List<CardCandidate>? alsoMatched = null)
    {
        var wanted = row.Variant;

        Describe(row, card);
        row.Status = ImportStatus.Matched;
        row.Candidates = Alternatives(card, alsoMatched);

        if (!string.Equals(row.Variant, wanted, StringComparison.Ordinal))
            row.Message = $"No '{wanted}' printing — using '{row.Variant}'.";
    }

    /// <summary>
    /// Copies a card's details onto the row without declaring the row settled.
    ///
    /// Split out from <see cref="Apply"/> for the mismatch case, which needs to show
    /// the card an id points at while withholding Matched: you cannot compare a scan
    /// against a thumbnail that was never drawn, and the whole point of flagging the
    /// row is to get a person to look at the picture.
    /// </summary>
    private static void Describe(ImportRow row, CardCandidate card)
    {
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
            row.Variant = card.Variants[0];
    }

    /// <summary>
    /// A catalogue row as a candidate. No market price, because the bulk data carries
    /// none and inventing one is the thing this app refuses to do — and a guessed
    /// printing list rather than a real one, exactly as the offline add path shows,
    /// since the daily refresh replaces both within a day of the card being owned.
    /// </summary>
    private static CardCandidate ToCandidate(CatalogueCard card) => new(
        CardId: card.Id,
        Name: card.Name,
        SetName: card.SetName,
        Number: card.Number,
        Rarity: card.Rarity,
        ImageSmall: card.HasImage ? $"/img/{card.Id}/small" : null,
        MarketPrice: null,
        Variants: Pricing.LikelyVariants(card.Rarity, card.ReleaseDate),
        PrintedTotal: card.PrintedTotal);

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
            Variants: Pricing.AvailableVariants(card),
            PrintedTotal: set.ValueKind == JsonValueKind.Object
                          && set.TryGetProperty("printedTotal", out var pt)
                          && pt.ValueKind == JsonValueKind.Number
                ? pt.GetInt32()
                : null);

        static string? Text(JsonElement el, string prop)
            => el.ValueKind == JsonValueKind.Object
               && el.TryGetProperty(prop, out var v)
               && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? v.ToString()
                : null;
    }

    // -------------------------------------------------------------------- commit

    public CommitResult Commit(string jobId, IReadOnlyList<CommitRow> rows)
    {
        // Refuse a commit aimed at a different collection than the one the file was
        // read against. Switching vaults mid-import is the only way to reach this, and
        // going through with it would file the cards under the wrong person.
        if (_jobs.TryGetValue(jobId, out var owner) && owner.Vault != Data.CurrentVault.Id)
            return new CommitResult(0, [new CommitOutcome(
                0, "", false, "That import was started against a different collection.")]);

        var outcomes = new List<CommitOutcome>();
        var seeded = new List<string>();
        var added = 0;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CardId))
            {
                outcomes.Add(new CommitOutcome(row.Index, "", false, "No card was chosen for this row."));
                continue;
            }

            // A card resolved from the offline catalogue has no payload yet, and until
            // this existed the row was simply skipped: the commit reported a smaller
            // number than you selected and never said which ones went missing. Seed it
            // the way the single-card add does, then let the enrichment pass fetch the
            // real prices and printings behind you.
            if (!cache.Has(row.CardId))
            {
                var payload = catalogue.IsUsable ? catalogue.BuildPayload(row.CardId) : null;
                if (payload is null)
                {
                    outcomes.Add(new CommitOutcome(
                        row.Index, row.CardId, false,
                        "No local details for this card — re-run the import for this row."));
                    continue;
                }

                cache.Upsert(payload.Value);
                seeded.Add(row.CardId);
            }

            // Recorded on the first card that actually lands, so an import that adds
            // nothing leaves no empty batch behind to be reviewed or undone.
            if (added == 0) batches.Create(jobId);

            collection.Add(new AddEntryRequest(
                CardId: row.CardId,
                Quantity: row.Quantity,
                Variant: row.Variant,
                Condition: row.Condition,
                Grade: row.Grade,
                PurchasePrice: row.PurchasePrice,
                PurchaseDate: row.PurchaseDate,
                Notes: row.Notes,
                Location: row.Location,
                Language: row.Language),
                importBatch: jobId);

            // Same as a single add: give each imported card a starting price point.
            // A card seeded from the catalogue has no prices to record yet; the pass
            // below is what fills those in.
            snapshots.RecordCurrentPrices(row.CardId);
            outcomes.Add(new CommitOutcome(row.Index, row.CardId, true, null));
            added++;
        }

        // One paced pass rather than one background task per card: a hundred-row batch
        // would otherwise open a hundred simultaneous connections to an API that
        // struggles with one.
        snapshots.QueueEnrichMany(seeded);

        if (_jobs.TryGetValue(jobId, out var job)) job.State = "committed";
        return new CommitResult(added, outcomes);
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
