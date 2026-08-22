using System.Globalization;
using System.Text.RegularExpressions;

namespace CardVault.Services;

/// <summary>
/// A slab's certification, read out of the free text you typed.
///
/// The grade stays one string on the collection row rather than becoming columns of
/// its own. What you wrote is the record — "PSA 9", "BGS 9.5 (Black Label)", a bare
/// cert number — and this reads a company and a number out of it for display and
/// filtering. Deriving rather than storing means there is one source of truth and no
/// migration that could disagree with it.
///
/// Whether a card is graded is deliberately NOT a question for the parser: any grade
/// text at all means graded. You do not type in that box by accident, and a parser
/// that decided "slabbed" or a bare cert number wasn't really a slab would be
/// overruling you about your own card.
/// </summary>
public static partial class Grades
{
    /// <summary>
    /// The graders whose slabs turn up in practice, longest name first so "CGC" is
    /// not found inside a longer token before the longer one gets a chance.
    /// </summary>
    public static readonly IReadOnlyList<string> Companies =
        ["PSA", "BGS", "BCCG", "CGC", "SGC", "ACE", "TAG", "AGS", "GMA", "HGA"];

    /// <summary>True for any card carrying a grade, however it was written.</summary>
    public static bool IsGraded(string? grade) => !string.IsNullOrWhiteSpace(grade);

    /// <summary>
    /// The grading company, if the text names one.
    ///
    /// Anchored at the front so the letters have to start a word, and closed with
    /// "no letter after" rather than a word boundary — people write "psa10" without
    /// the space, and a boundary between "a" and "1" doesn't exist. A following
    /// letter still disqualifies it, which keeps "PSA" out of "psalm".
    /// </summary>
    public static string? Company(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return null;

        foreach (var company in Companies)
            if (Regex.IsMatch(grade, $@"\b{company}(?![A-Za-z])", RegexOptions.IgnoreCase))
                return company;

        return null;
    }

    /// <summary>
    /// The numeric grade, if there is one on the 1-10 scale every one of these
    /// companies uses. Half grades count; a cert number does not, which is what the
    /// range check is really for — an eight-digit serial is not a grade of 12,345,678.
    ///
    /// Returns null for "Authentic" and the other ungraded designations, which are
    /// real slabs with no number. Those stay graded, they just have nothing to sort on.
    /// </summary>
    public static double? Value(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return null;

        foreach (Match m in NumberPattern().Matches(grade))
        {
            if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                continue;
            if (n is >= 1 and <= 10) return n;
        }

        return null;
    }

    /// <summary>
    /// "PSA 9" back out of whatever was typed, for a badge with no room for the rest.
    /// Falls back to the original text when there is nothing to tidy, so a slab is
    /// never described by a blank.
    /// </summary>
    public static string? Label(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return null;

        var company = Company(grade);
        var value = Value(grade);

        if (company is not null && value is not null)
            return $"{company} {value.Value.ToString("0.#", CultureInfo.InvariantCulture)}";

        return grade.Trim();
    }

    /// <summary>
    /// A number standing on its own, whole or with one decimal place.
    ///
    /// The lookarounds are what keep a cert number out. Without them "09876543" hands
    /// back a leading "09", which is a perfectly plausible grade of 9 taken from the
    /// front of a serial — so a digit on either side disqualifies the match entirely
    /// rather than letting it be trimmed to something in range.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2}(?:\.\d)?(?!\d)")]
    private static partial Regex NumberPattern();
}
