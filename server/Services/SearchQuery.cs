using System.Text;
using System.Text.RegularExpressions;

namespace CardVault.Services;

/// <summary>
/// What someone meant by what they typed, before it's aimed at anything.
///
/// Parsing and rendering are separate because the same box now feeds two very
/// different things: a pokemontcg.io query string, and SQL against the offline
/// catalogue. Working out that "056/094" is a collector number is the hard part and
/// there should only ever be one copy of it.
/// </summary>
/// <param name="Raw">
/// Set when the input used the API's own field syntax, which is passed through
/// untouched. The catalogue can't honour arbitrary API fields, so this is also the
/// signal that a query can't be served locally.
/// </param>
public sealed record SearchIntent(
    string? Name,
    string? Number,
    int? PrintedTotal,
    string? SetId,
    string? Raw)
{
    /// <summary>True when there's nothing to search for at all.</summary>
    public bool IsEmpty => Name is null && Number is null && PrintedTotal is null && SetId is null && Raw is null;

    /// <summary>
    /// Whether the offline catalogue can answer this. Raw API syntax can't be
    /// translated field-for-field, so those queries stay with the API.
    /// </summary>
    public bool CanRunLocally => Raw is null && !IsEmpty;
}

/// <summary>
/// Turns what someone typed into a pokemontcg.io query.
///
/// The important case is working through a physical stack: you're holding a card
/// reading "4/102", so typing that should find that card. Treating a bare number
/// as part of a card's *name* — which is what a naive wildcard search does — is
/// almost never what was meant.
///
/// The denominator does most of the work. It's the set's printed total, and both
/// the API and the offline catalogue can filter on it directly, so "4/102" narrows
/// 20,000-odd cards to about two without needing to know which set that is.
/// </summary>
public static partial class SearchQuery
{
    /// <summary>
    /// A collector number: optional short prefix, digits, optional suffix letter —
    /// "4", "58", "TG12", "SV49", "H5", "SWSH039".
    ///
    /// The prefix is capped so real card names that happen to end in a digit
    /// ("Porygon2") aren't mistaken for numbers. Four characters, not three: the
    /// promo sets number their cards "SWSH039" and "HGSS01", and at three those
    /// fell through to a name search that could never match them.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z]{0,4}\d+[A-Za-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectorNumber();

    /// <summary>"4/102", "SV49/SV94", "102 / 102" — number over set total.</summary>
    [GeneratedRegex(@"^(?<num>[A-Za-z]{0,4}\d+[A-Za-z]?)\s*/\s*(?<total>[A-Za-z]{0,4}\d+[A-Za-z]?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberOverTotal();

    /// <summary>Works out what was meant. Rendering it is someone else's job.</summary>
    public static SearchIntent Parse(string? input, string? setId)
    {
        var text = input?.Trim() ?? "";
        var set = string.IsNullOrWhiteSpace(setId) ? null : Clean(setId);

        if (text.Length == 0) return new SearchIntent(null, null, null, set, null);

        // Anything with a colon is the API's own syntax — pass it through so power
        // users keep full access to fields we don't special-case.
        if (text.Contains(':')) return new SearchIntent(null, null, null, set, text);

        // "4/102" — number plus the set's printed total.
        if (NumberOverTotal().Match(text) is { Success: true } slash)
        {
            var (number, total) = NumberAndTotal(slash);
            return new SearchIntent(null, number, total, set, null);
        }

        // "4", "056", "TG12" — a bare collector number.
        if (CollectorNumber().IsMatch(text))
            return new SearchIntent(null, Number(text), null, set, null);

        // "charizard 4" or "charizard 4/102" — a name with a number after it.
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var head = Clean(text[..lastSpace]);
            var tail = text[(lastSpace + 1)..].Trim();

            if (NumberOverTotal().Match(tail) is { Success: true } tailSlash)
            {
                var (number, total) = NumberAndTotal(tailSlash);
                return new SearchIntent(head, number, total, set, null);
            }

            if (CollectorNumber().IsMatch(tail))
                return new SearchIntent(head, Number(tail), null, set, null);
        }

        return new SearchIntent(Clean(text), null, null, set, null);
    }

    /// <summary>The pokemontcg.io form.</summary>
    public static string? Build(string? input, string? setId)
    {
        var intent = Parse(input, setId);
        var parts = new List<string>();

        if (intent.Raw is { } raw) parts.Add(raw);
        else if (intent.Name is { } name) parts.Add($"name:\"*{name}*\"");

        if (intent.Number is { } number) parts.Add($"number:{number}");
        if (intent.PrintedTotal is { } total) parts.Add($"set.printedTotal:{total}");
        if (intent.SetId is { } set) parts.Add($"set.id:{set}");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// The same intent as a WHERE clause over the offline catalogue, with its
    /// parameters. Names match on substring, the way the API's wildcard search does,
    /// so the two paths return the same cards for the same typing.
    ///
    /// Returns null for a query the catalogue can't answer, which is the caller's
    /// cue to fall back to the API rather than to return nothing.
    /// </summary>
    public static (string Where, Dictionary<string, object> Parameters)? ToSql(SearchIntent intent)
    {
        if (!intent.CanRunLocally) return null;

        var clauses = new List<string>();
        var parameters = new Dictionary<string, object>();

        if (intent.Name is { } name)
        {
            // LIKE is case-insensitive for ASCII in SQLite by default, which matches
            // how the API treats a name search.
            clauses.Add(@"name LIKE $name ESCAPE '\'");
            parameters["$name"] = $"%{Escape(name)}%";
        }

        if (intent.Number is { } number)
        {
            // Stored as printed, so "4" must not also match "14" or "40".
            clauses.Add("number = $number COLLATE NOCASE");
            parameters["$number"] = number;
        }

        if (intent.PrintedTotal is { } total)
        {
            clauses.Add("printed_total = $printedTotal");
            parameters["$printedTotal"] = total;
        }

        if (intent.SetId is { } set)
        {
            clauses.Add("set_id = $setId COLLATE NOCASE");
            parameters["$setId"] = set;
        }

        return clauses.Count == 0 ? null : (string.Join(" AND ", clauses), parameters);
    }

    private static (string Number, int? Total) NumberAndTotal(Match match)
    {
        var number = Number(match.Groups["num"].Value);
        var totalText = match.Groups["total"].Value;

        // Only numeric totals map to a printed total. Some modern subsets print a
        // non-numeric denominator ("SV49/SV94"), which nothing can filter on.
        return totalText.All(char.IsAsciiDigit) && int.TryParse(totalText, out var total)
            ? (number, total)
            : (number, null);
    }

    /// <summary>
    /// A collector number as the catalogue stores it, which is not always how the
    /// card prints it.
    ///
    /// Cards read "056/094", but the number is held as "56" — so typing what's
    /// printed on the card, which is the whole point of this parser, found nothing.
    /// Leading zeros are dropped from a purely numeric one to close that gap. The
    /// API confirms nothing is lost: no card anywhere has a numeric collector
    /// number with a leading zero, so "56" is the only form that can match.
    ///
    /// Prefixed numbers keep theirs. "SWSH001" is stored with its zeros intact and
    /// "SWSH1" matches nothing at all, so trimming there would break every promo
    /// it touched.
    /// </summary>
    private static string Number(string value)
    {
        var cleaned = Clean(value);
        if (!cleaned.All(char.IsAsciiDigit)) return cleaned;

        var trimmed = cleaned.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>Strips characters that would break out of the query's own syntax.</summary>
    private static string Clean(string value)
        => value.Replace("\"", "").Replace("\\", "").Replace("(", "").Replace(")", "").Trim();

    /// <summary>
    /// Neutralises LIKE's own wildcards so a card name containing % or _ searches
    /// for those characters rather than matching everything.
    /// </summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
