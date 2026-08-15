using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// The cross-check that stands between a misread set symbol and a confidently
/// imported wrong card.
///
/// It has two failure modes and they pull against each other. Missing a real
/// contradiction imports the wrong card silently, which is the whole thing this
/// exists to prevent. Flagging cards that are perfectly fine is not harmless either:
/// a check that cries wolf across a hundred-row batch is a check that stops being
/// read, and then it may as well not exist. Both directions are pinned here.
/// </summary>
public class CardIdCheckTests
{
    private static string? Check(string? cardName, string? cardNumber, string? claimedName, string? claimedNumber)
        => CardIdCheck.Contradiction("base1-4", cardName, cardNumber, claimedName, claimedNumber);

    // ------------------------------------------------- contradictions worth raising

    [Fact]
    public void A_different_card_name_is_a_contradiction()
    {
        var conflict = Check("Charizard", "4", "Blastoise", "4");

        Assert.NotNull(conflict);
        Assert.Contains("Blastoise", conflict);
        Assert.Contains("Charizard", conflict);
        // Naming the id matters: the message is read next to a hundred others.
        Assert.Contains("base1-4", conflict);
    }

    [Fact]
    public void A_different_number_is_a_contradiction()
    {
        var conflict = Check("Charizard", "4", "Charizard", "17");

        Assert.NotNull(conflict);
        Assert.Contains("17", conflict);
    }

    [Fact]
    public void The_name_is_reported_ahead_of_the_number_when_both_disagree()
        // Both are wrong, so the id is simply the wrong card — say the useful half.
        => Assert.Contains("Blastoise", Check("Charizard", "4", "Blastoise", "17")!);

    // ------------------------------------------------------- agreement, left alone

    [Fact]
    public void An_exact_match_is_not_a_contradiction()
        => Assert.Null(Check("Charizard", "4", "Charizard", "4"));

    [Fact]
    public void A_suffix_the_scan_missed_is_not_a_contradiction()
    {
        // A model reading artwork routinely returns the bare species name. Flagging
        // every ex, V and VMAX in the batch would bury the handful that matter.
        Assert.Null(Check("Charizard ex", "4", "Charizard", "4"));
        Assert.Null(Check("Charizard", "4", "Charizard ex", "4"));
        Assert.Null(Check("Iron Valiant ex", "89", "Iron Valiant", "89"));
    }

    [Fact]
    public void Case_and_punctuation_do_not_disagree()
    {
        Assert.Null(Check("Farfetch'd", "27", "Farfetchd", "27"));
        Assert.Null(Check("Mr. Mime", "6", "MR MIME", "6"));
        Assert.Null(Check("Ho-Oh", "22", "Ho Oh", "22"));
    }

    [Fact]
    public void A_padded_number_is_the_same_number()
    {
        Assert.Null(Check("Charizard", "4", "Charizard", "004"));
        Assert.Null(Check("Charizard", "004", "Charizard", "4"));
    }

    [Fact]
    public void Card_zero_survives_being_trimmed()
        // "000" trimmed of leading zeros is empty, which must not become a match for
        // everything or a mismatch against itself.
        => Assert.Null(Check("Something", "000", "Something", "0"));

    // --------------------------------------------- nothing to check is not a failure

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    public void A_row_that_claims_nothing_cannot_contradict_anything(string? name, string? number)
        // An id-only CSV is a legitimate thing to upload. It just gets no second
        // opinion, which is a reason to include a name rather than a reason to fail.
        => Assert.Null(Check("Charizard", "4", name, number));

    [Fact]
    public void A_card_with_no_number_of_its_own_is_not_contradicted_by_one()
        => Assert.Null(Check("Charizard", null, "Charizard", "4"));

    [Fact]
    public void A_card_with_no_name_of_its_own_is_not_contradicted_by_one()
        => Assert.Null(Check(null, "4", "Charizard", "4"));

    // ----------------------------------------------------- the alphanumeric numbers

    [Fact]
    public void Promo_style_numbers_still_compare()
    {
        Assert.Null(Check("Pikachu", "SWSH039", "Pikachu", "swsh039"));
        Assert.NotNull(Check("Pikachu", "SWSH039", "Pikachu", "SWSH040"));
    }

    [Fact]
    public void A_number_that_differs_only_by_a_trailing_letter_is_a_contradiction()
        // "TG04" and "TG04a" are two different cards in the same subset.
        => Assert.NotNull(Check("Rayquaza", "TG04", "Rayquaza", "TG04a"));
}
