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
    /// "4", "58", "TG12", "SV49", "H5".
    ///
    /// The prefix is capped at three characters so real card names that happen to
    /// end in a digit ("Porygon2") aren't mistaken for numbers.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z]{0,3}\d+[A-Za-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectorNumber();

    /// <summary>"4/102", "SV49/SV94", "102 / 102" — number over set total.</summary>
    [GeneratedRegex(@"^(?<num>[A-Za-z]{0,3}\d+[A-Za-z]?)\s*/\s*(?<total>[A-Za-z]{0,3}\d+[A-Za-z]?)$",
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

        // "4", "TG12" — a bare collector number.
        if (CollectorNumber().IsMatch(text))
            return [$"number:{Clean(text)}"];

        // "charizard 4" or "charizard 4/102" — a name with a number after it.
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var head = text[..lastSpace].Trim();
            var tail = text[(lastSpace + 1)..].Trim();

            if (NumberOverTotal().Match(tail) is { Success: true } tailSlash)
                return [Name(head), .. FromNumberAndTotal(tailSlash.Groups["num"].Value, tailSlash.Groups["total"].Value)];

            if (CollectorNumber().IsMatch(tail))
                return [Name(head), $"number:{Clean(tail)}"];
        }

        return [Name(text)];
    }

    private static IEnumerable<string> FromNumberAndTotal(string number, string total)
    {
        yield return $"number:{Clean(number)}";

        // Only numeric totals map to set.printedTotal. Some modern subsets print a
        // non-numeric denominator ("SV49/SV94"), which this field can't match.
        if (total.All(char.IsDigit)) yield return $"set.printedTotal:{total}";
    }

    private static string Name(string value) => $"name:\"*{Clean(value)}*\"";

    /// <summary>Strips characters that would break out of the query's own syntax.</summary>
    private static string Clean(string value)
        => value.Replace("\"", "").Replace("\\", "").Replace("(", "").Replace(")", "").Trim();
}
