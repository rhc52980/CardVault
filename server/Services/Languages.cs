namespace CardVault.Services;

/// <summary>
/// The language a card was printed in.
///
/// Stored as a lowercase ISO 639-1 code — the same shape Scrydex takes — with the
/// two Chinese printings distinguished by script, because they are separate print
/// runs and collectors do not treat them as interchangeable.
///
/// This is a property of the copy you own, not of the card: the catalogue entry for
/// Charizard describes the English printing, and the Japanese one is the same card
/// in a different language, not a different card. Keeping it on the collection row
/// is what lets you own both without the vault thinking they are duplicates.
/// </summary>
public static class Languages
{
    public const string Default = "en";

    /// <summary>
    /// Every language the TCG is officially printed in, in the order they're offered.
    /// English first because it's the default and the catalogue's own language;
    /// Japanese second because it's far and away the most collected of the rest.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("en", "English"),
        ("ja", "Japanese"),
        ("fr", "French"),
        ("de", "German"),
        ("it", "Italian"),
        ("es", "Spanish"),
        ("pt", "Portuguese"),
        ("ko", "Korean"),
        ("zh-tw", "Chinese (Traditional)"),
        ("zh-cn", "Chinese (Simplified)"),
        ("id", "Indonesian"),
        ("th", "Thai"),
        ("ru", "Russian"),
    ];

    private static readonly Dictionary<string, string> ByCode =
        All.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a CSV might call each language. Names and two-letter codes are handled
    /// generically below; this covers the aliases that aren't either — country codes
    /// people reach for ("jp" for Japanese), and the endonyms.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jp"] = "ja",
        ["jpn"] = "ja",
        ["jap"] = "ja",
        ["nihongo"] = "ja",
        ["eng"] = "en",
        ["us"] = "en",
        ["uk"] = "en",
        ["fra"] = "fr",
        ["fre"] = "fr",
        ["francais"] = "fr",
        ["français"] = "fr",
        ["ger"] = "de",
        ["deu"] = "de",
        ["deutsch"] = "de",
        ["ita"] = "it",
        ["italiano"] = "it",
        ["spa"] = "es",
        ["esp"] = "es",
        ["espanol"] = "es",
        ["español"] = "es",
        ["por"] = "pt",
        ["ptbr"] = "pt",
        ["pt-br"] = "pt",
        ["portugues"] = "pt",
        ["português"] = "pt",
        ["kor"] = "ko",
        ["korea"] = "ko",
        ["zht"] = "zh-tw",
        ["zh_tw"] = "zh-tw",
        ["tw"] = "zh-tw",
        ["taiwan"] = "zh-tw",
        ["traditional chinese"] = "zh-tw",
        ["chinese traditional"] = "zh-tw",
        ["zhs"] = "zh-cn",
        ["zh_cn"] = "zh-cn",
        ["cn"] = "zh-cn",
        ["zh"] = "zh-cn",
        ["china"] = "zh-cn",
        ["simplified chinese"] = "zh-cn",
        ["chinese simplified"] = "zh-cn",
        ["chinese"] = "zh-cn",
        ["ind"] = "id",
        ["indo"] = "id",
        ["tha"] = "th",
        ["rus"] = "ru",
    };

    private static readonly Dictionary<string, string> ByName =
        All.ToDictionary(l => l.Name, l => l.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns whatever a CSV column or an old client sent into a code we store.
    ///
    /// Anything unrecognised falls back to English rather than being kept verbatim:
    /// a free-text language would defeat the point of the column, which is that
    /// "Japanese", "JP" and "ja" have to end up as the same thing before they can be
    /// filtered on or counted.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var v = raw?.Trim() ?? "";
        if (v.Length == 0) return Default;

        if (ByCode.ContainsKey(v)) return v.ToLowerInvariant();
        if (ByName.TryGetValue(v, out var fromName)) return fromName;
        if (Aliases.TryGetValue(v, out var fromAlias)) return fromAlias;

        // "Japanese (JP)", "English - NM" and similar: try the leading word on its own.
        var head = v.Split([' ', '(', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (head is not null && head.Length != v.Length)
        {
            if (ByCode.ContainsKey(head)) return head.ToLowerInvariant();
            if (ByName.TryGetValue(head, out var h)) return h;
            if (Aliases.TryGetValue(head, out var a)) return a;
        }

        return Default;
    }

    /// <summary>"ja" -> "Japanese". Unknown codes are handed back as they came.</summary>
    public static string Name(string? code)
        => code is not null && ByCode.TryGetValue(code, out var name) ? name : code ?? "";

    /// <summary>
    /// Whether a catalogue price describes this copy.
    ///
    /// The catalogue is English-only, and TCGplayer and Cardmarket both come through
    /// it, so every figure it yields is for the English printing. A Japanese card's
    /// market is a different market at a different price, and pinning the English
    /// figure to it would be the same mistake as adding euros to dollars. Non-English
    /// copies are valued by hand or not at all.
    ///
    /// Hand-entered items don't go through here: they're priced by an eBay search on
    /// the name you gave them, which describes whatever you actually typed.
    /// </summary>
    public static bool IsPriced(string? code) => Normalize(code) == Default;
}
