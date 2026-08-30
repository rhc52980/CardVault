using CardVault.Models;
using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// A matched import row is settled, but narrowing may have chosen between several real
/// printings on thin evidence — a set name the file guessed at, or nothing but a
/// denominator two sets happen to share. 153/189 is both Darkness Ablaze and Astral
/// Radiance, and picking silently is how a collection ends up holding the wrong one.
///
/// So the alternatives come back with the row. The other half of the promise matters
/// just as much: a row with only one possible card must look exactly as it did before,
/// or every line of a hundred-row import grows a picker offering no choice.
/// </summary>
public class ImportAlternativeTests
{
    private static CardCandidate Card(string id, string name, string set, string number = "153")
        => new(id, name, set, number, Rarity: null, ImageSmall: null,
               MarketPrice: null, Variants: [], PrintedTotal: 189);

    private static readonly CardCandidate DarknessAblaze = Card("swsh3-153", "Greedent", "Darkness Ablaze");
    private static readonly CardCandidate AstralRadiance = Card("swsh10-153", "Greedent", "Astral Radiance");

    // ----------------------------------------------------------- when there is a choice

    [Fact]
    public void Two_printings_of_the_same_number_are_both_offered()
    {
        var alternatives = ImportService.Alternatives(
            DarknessAblaze, [DarknessAblaze, AstralRadiance]);

        Assert.Equal(2, alternatives.Count);
        Assert.Contains(alternatives, c => c.SetName == "Astral Radiance");
    }

    /// <summary>
    /// The chosen card leads, so the picker opens showing what was actually matched
    /// rather than making you find it in the list to confirm nothing has changed.
    /// </summary>
    [Fact]
    public void The_matched_card_comes_first()
    {
        var alternatives = ImportService.Alternatives(
            AstralRadiance, [DarknessAblaze, AstralRadiance]);

        Assert.Equal("swsh10-153", alternatives[0].CardId);
    }

    /// <summary>
    /// A bare number with no denominator can match a great many cards, and a list that
    /// long is not a choice either.
    /// </summary>
    [Fact]
    public void A_very_long_list_is_capped()
    {
        var many = Enumerable.Range(1, 40)
            .Select(i => Card($"set{i}-153", "Greedent", $"Set {i}"))
            .ToList();

        Assert.Equal(12, ImportService.Alternatives(many[0], many).Count);
    }

    // -------------------------------------------------------- when there is no choice

    /// <summary>
    /// The half that keeps a hundred-row import readable. One candidate is not an
    /// alternative, and the row must look exactly as it always did.
    /// </summary>
    [Fact]
    public void A_single_match_offers_nothing()
        => Assert.Empty(ImportService.Alternatives(DarknessAblaze, [DarknessAblaze]));

    [Fact]
    public void Nothing_found_offers_nothing()
        => Assert.Empty(ImportService.Alternatives(DarknessAblaze, null));

    [Fact]
    public void An_empty_list_offers_nothing()
        => Assert.Empty(ImportService.Alternatives(DarknessAblaze, []));

    // ------------------------------------------------------------- through Apply

    [Fact]
    public void Applying_a_row_with_alternatives_still_matches_it()
    {
        var row = new ImportRow { Index = 0, Source = "x" };

        ImportService.Apply(row, DarknessAblaze, [DarknessAblaze, AstralRadiance]);

        Assert.Equal(ImportStatus.Matched, row.Status);
        Assert.Equal("swsh3-153", row.CardId);
        Assert.Equal(2, row.Candidates.Count);
    }

    [Fact]
    public void Applying_an_unambiguous_row_leaves_it_bare()
    {
        var row = new ImportRow { Index = 0, Source = "x" };

        ImportService.Apply(row, DarknessAblaze, [DarknessAblaze]);

        Assert.Equal(ImportStatus.Matched, row.Status);
        Assert.Empty(row.Candidates);
    }
}
