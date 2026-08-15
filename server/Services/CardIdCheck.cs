namespace CardVault.Services;

/// <summary>
/// Checks a card id against what was transcribed off the same card.
///
/// A Pokémon card does not carry its pokemontcg.io id anywhere on it — the printed
/// identifiers are a name, a collector number and a set symbol. So anything filling
/// in a Card ID column is <em>inferring</em> that id, and an inferred id that is
/// wrong is still perfectly valid: it resolves to exactly one real card and would
/// otherwise arrive in the review list looking every bit as matched as a correct one.
///
/// Comparing the id against the name and number read off the same card is the only
/// opportunity to notice. It is a cheap check, but it is the difference between
/// scanning a few thousand cards and trusting the result, and scanning a few thousand
/// cards and hoping.
/// </summary>
public static class CardIdCheck
{
    /// <summary>
    /// How the card an id resolved to is contradicted by what the row said, or null
    /// when they agree — or when the row said nothing that could disagree.
    /// </summary>
    public static string? Contradiction(
        string cardId, string? cardName, string? cardNumber, string? claimedName, string? claimedNumber)
    {
        if (!string.IsNullOrWhiteSpace(claimedName) && !string.IsNullOrWhiteSpace(cardName))
        {
            var claimed = Simplify(claimedName);
            var actual = Simplify(cardName);

            // Containment rather than equality, so "Charizard" against "Charizard ex"
            // reads as agreement. Being noisy here carries a real cost of its own: a
            // check that cries wolf is a check that stops being read, and every false
            // flag is a card someone has to pick up and look at again.
            if (claimed.Length > 0 && actual.Length > 0
                && !claimed.Contains(actual) && !actual.Contains(claimed))
                return $"the row reads \"{claimedName.Trim()}\" but {cardId} is \"{cardName.Trim()}\"";
        }

        if (!string.IsNullOrWhiteSpace(claimedNumber) && !string.IsNullOrWhiteSpace(cardNumber)
            && !string.Equals(TrimNumber(claimedNumber), TrimNumber(cardNumber), StringComparison.Ordinal))
            return $"the row reads number {claimedNumber.Trim()} but {cardId} is #{cardNumber.Trim()}";

        return null;
    }

    /// <summary>Letters and digits only, so spacing and punctuation can't disagree.</summary>
    private static string Simplify(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>"004" and "4" are the same card number written twice.</summary>
    private static string TrimNumber(string value)
    {
        var trimmed = value.Trim().TrimStart('0');
        return (trimmed.Length == 0 ? "0" : trimmed).ToLowerInvariant();
    }
}
