using System.Text.Json;

namespace CardVault.Services;

/// <summary>
/// What you're allowed to play a card in, and the rules a deck has to satisfy.
///
/// Legality is not worked out here — it's published per card by the API and read
/// straight off the payload. That matters: rotation moves every year and sets leave
/// Standard on a schedule nobody would want hard-coded, so the only maintainable
/// answer is the one that arrives with the card.
/// </summary>
public static class Formats
{
    public const string Standard = "standard";
    public const string Expanded = "expanded";

    /// <summary>
    /// "Anything goes" — no rotation, no deck rules enforced. Not a format the API
    /// reports on, which is why it's handled here rather than looked up.
    /// </summary>
    public const string Unlimited = "unlimited";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        (Standard, "Standard"),
        (Expanded, "Expanded"),
        (Unlimited, "Unlimited"),
    ];

    /// <summary>A constructed deck is exactly this many cards. Not at least — exactly.</summary>
    public const int DeckSize = 60;

    /// <summary>
    /// How many copies of one card a deck may hold. Basic Energy is the exception and
    /// has no limit, which is why a deck of 20 Fire Energy is legal and 20 Pikachu is not.
    /// </summary>
    public const int CopyLimit = 4;

    public static string Normalize(string? raw)
    {
        var v = raw?.Trim().ToLowerInvariant() ?? "";
        return All.Any(f => f.Id == v) ? v : Standard;
    }

    public static string Name(string? id)
        => All.FirstOrDefault(f => f.Id == Normalize(id)).Name ?? "Standard";

    /// <summary>
    /// What the card itself says about a format — "Legal", "Banned", or null when the
    /// API doesn't mention it, which for Standard means rotated out.
    /// </summary>
    public static string? Legality(JsonElement card, string format)
        => card.TryGetProperty("legalities", out var l) && l.ValueKind == JsonValueKind.Object
           && l.TryGetProperty(format, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Whether a card may be played in a format at all. Unlimited allows everything;
    /// for the others the card has to say "Legal" in as many words. Silence is a no —
    /// a card the API doesn't list for Standard has rotated, and treating an absent
    /// answer as permission would quietly bless a deck that can't be played.
    /// </summary>
    public static bool IsLegal(JsonElement card, string format)
        => Normalize(format) == Unlimited
           || string.Equals(Legality(card, Normalize(format)), "Legal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Basic Energy, the one card type you may hold any number of. Identified by
    /// supertype and subtype rather than by name: "Fire Energy" is basic and
    /// "Twin Energy" is not, and only the subtypes tell them apart.
    /// </summary>
    public static bool IsBasicEnergy(JsonElement card)
    {
        if (!string.Equals(Str(card, "supertype"), "Energy", StringComparison.OrdinalIgnoreCase))
            return false;

        return card.TryGetProperty("subtypes", out var s)
               && s.ValueKind == JsonValueKind.Array
               && s.EnumerateArray().Any(x =>
                   x.ValueKind == JsonValueKind.String
                   && string.Equals(x.GetString(), "Basic", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
