using System.Text.RegularExpressions;

namespace CardVault.Services;

/// <summary>
/// Turns what someone typed into a pokemontcg.io query.
///
/// The important case is working through a physical stack: you're holding a card
/// reading "4/102", so typing that should find that card. Treating a bare number
/// as part of a card's *name* — which is what a naive wildcard search does — is
/// almost never what was meant.
///
/// The denominator does most of the work. It's the set's printed total, and the
/// API can filter on it directly, so "4/102" narrows 20,000-odd cards to about two
/// without needing to know which set that is.
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

    public static string? Build(string? input, string? setId)
    {
        var parts = new List<string>();
        var text = input?.Trim() ?? "";

        if (text.Length > 0)
        {
            // Anything with a colon is the API's own syntax — pass it through so
            // power users keep full access to fields we don't special-case.
            if (text.Contains(':')) parts.Add(text);
            else parts.AddRange(Interpret(text));
        }

        if (!string.IsNullOrWhiteSpace(setId)) parts.Add($"set.id:{Clean(setId)}");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static IEnumerable<string> Interpret(string text)
    {
        // "4/102" — number plus the set's printed total.
        if (NumberOverTotal().Match(text) is { Success: true } slash)
            return FromNumberAndTotal(slash.Groups["num"].Value, slash.Groups["total"].Value);

        // "4", "056", "TG12" — a bare collector number.
        if (CollectorNumber().IsMatch(text))
            return [$"number:{Number(text)}"];

        // "charizard 4" or "charizard 4/102" — a name with a number after it.
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var head = text[..lastSpace].Trim();
            var tail = text[(lastSpace + 1)..].Trim();

            if (NumberOverTotal().Match(tail) is { Success: true } tailSlash)
                return [Name(head), .. FromNumberAndTotal(tailSlash.Groups["num"].Value, tailSlash.Groups["total"].Value)];

            if (CollectorNumber().IsMatch(tail))
                return [Name(head), $"number:{Number(tail)}"];
        }

        return [Name(text)];
    }

    private static IEnumerable<string> FromNumberAndTotal(string number, string total)
    {
        yield return $"number:{Number(number)}";

        // Only numeric totals map to set.printedTotal. Some modern subsets print a
        // non-numeric denominator ("SV49/SV94"), which this field can't match.
        // Normalised for the same reason as the numerator: printedTotal is a number,
        // and "094" only matches today because the API happens to coerce it.
        if (total.All(char.IsDigit)) yield return $"set.printedTotal:{Number(total)}";
    }

    private static string Name(string value) => $"name:\"*{Clean(value)}*\"";

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
}
