using CardVault.Models;
using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// Which header names the importer understands, and what it says about the ones it
/// doesn't.
///
/// A column that isn't recognised is dropped, and dropping one silently is the worst
/// failure this importer has: every row still resolves, still reports Matched, and is
/// still wrong. It happened for real — files headed Set_Number imported for months
/// with the number never read, so each row was settled on the card's name alone and
/// picked whichever printing came back first. A file naming the right card imported
/// the wrong one, confidently, and nothing in the result looked amiss.
/// </summary>
public class ImportColumnTests
{
    private static (Dictionary<string, int> Map, List<string> Unmapped) Map(params string[] header)
        => ImportService.MapColumns(header);

    // ------------------------------------------------- the header that caused this

    /// <summary>
    /// The exact header from the scanning pipeline. "Set number" is an ordinary name
    /// for a collector number — it is the number within the set — and its absence from
    /// the aliases is what threw the numbers away.
    /// </summary>
    [Fact]
    public void The_scanner_header_maps_every_field_that_matters()
    {
        var (map, unmapped) = Map(
            "File_Name", "Card_Name", "Set_Name", "Set_Number", "Rarity", "Card_Type", "Language", "Notes");

        Assert.Equal(1, map["name"]);
        Assert.Equal(2, map["setName"]);
        Assert.Equal(3, map["number"]);
        Assert.Equal(6, map["language"]);
        Assert.DoesNotContain("Set_Number", unmapped);
    }

    [Theory]
    [InlineData("Set_Number")]
    [InlineData("Set Number")]
    [InlineData("setnumber")]
    [InlineData("Card No")]
    [InlineData("Collector Number")]
    [InlineData("Number")]
    [InlineData("#")]
    public void A_collector_number_is_recognised_however_it_is_written(string header)
        => Assert.True(Map(header).Map.ContainsKey("number"), $"'{header}' was not read as a number");

    /// <summary>
    /// Set_Name and Set_Number differ by one word and mean entirely different things.
    /// Matching either loosely enough to catch the other would be worse than missing it.
    /// </summary>
    [Fact]
    public void The_set_name_and_the_set_number_do_not_collide()
    {
        var (map, _) = Map("Set_Name", "Set_Number");

        Assert.Equal(0, map["setName"]);
        Assert.Equal(1, map["number"]);
    }

    // ------------------------------------------------------- what gets said out loud

    /// <summary>
    /// The half that makes the next one of these visible. The warning already existed;
    /// it read "Ignored unrecognised columns: File_Name, Set_Number, Rarity, Card_Type"
    /// in small grey text, and the one that mattered was hidden by the three that
    /// didn't. Anything whose name sounds like a field the importer wants is worth
    /// saying loudly, separately, above the rows.
    /// </summary>
    [Theory]
    [InlineData("Set_Number")]
    [InlineData("Card Name")]
    [InlineData("Qty")]
    [InlineData("Price Paid")]
    [InlineData("Condition")]
    [InlineData("Printing Language")]
    public void A_column_that_sounds_important_is_flagged(string column)
        => Assert.True(ImportService.LooksImportant(column), $"'{column}' should have been flagged");

    /// <summary>
    /// And the other half: a warning that fires on everything is the grey line again.
    /// These are columns a scan file genuinely carries and the importer has no use for.
    /// </summary>
    [Theory]
    [InlineData("File_Name")]
    [InlineData("Rarity")]
    [InlineData("Card_Type")]
    [InlineData("Notes")]
    [InlineData("Artist")]
    public void An_ordinary_extra_column_is_not(string column)
        => Assert.False(ImportService.LooksImportant(column), $"'{column}' should not have been flagged");
}

/// <summary>
/// Whether the card that came back is the card the file described.
///
/// A lookup returning exactly one card skips narrowing — there is nothing to choose
/// between — and so nothing used to check that the one card was the right one. A row
/// reading "Gastly, Crimson Invasion, 36/111" matched Crocalor #36 from Paldea Evolved
/// and reported it settled, with Gastly #36 sitting in the catalogue all along.
///
/// The name must still yield to the number: it is the field that gets misread, and the
/// whole point of leading with the number is that a bad name cannot hide the right
/// card. So this is not a filter — it is the difference between a misreading and a
/// contradiction, and only the second is worth stopping for.
/// </summary>
public class NameAgreementTests
{
    [Theory]
    [InlineData("Gastly", "Crocalor")]
    [InlineData("Purrloin", "Sandygast")]
    [InlineData("Gastly", "Haunter")]
    public void An_unrelated_name_is_a_contradiction(string claimed, string actual)
        => Assert.False(ImportService.NamesAgree(claimed, actual));

    /// <summary>The misreadings this must keep tolerating.</summary>
    [Theory]
    [InlineData("Grimmsnari", "Grimmsnarl")]
    [InlineData("Purrl0in", "Purrloin")]
    [InlineData("Sizzlipede ", "Sizzlipede")]
    [InlineData("galarian runerigus", "Galarian Runerigus")]
    [InlineData("Mr Mime", "Mr. Mime")]
    public void A_misreading_still_agrees(string claimed, string actual)
        => Assert.True(ImportService.NamesAgree(claimed, actual), $"'{claimed}' vs '{actual}'");

    /// <summary>
    /// A file naming the Pokémon where the card carries an owner's prefix is describing
    /// that card, not disagreeing with it.
    /// </summary>
    [Theory]
    [InlineData("Purrloin", "N's Purrloin")]
    [InlineData("Zoroark", "Hisuian Zoroark")]
    public void A_shorter_name_of_the_same_card_agrees(string claimed, string actual)
        => Assert.True(ImportService.NamesAgree(claimed, actual));

    /// <summary>
    /// Short names get a tighter allowance. Two edits on six letters reaches most of
    /// the Pokédex, and an allowance that generous stops being a check at all.
    /// </summary>
    [Fact]
    public void Short_names_are_held_to_one_edit()
    {
        Assert.True(ImportService.NamesAgree("Gastly", "Gastl"));
        Assert.False(ImportService.NamesAgree("Gastly", "Gengar"));
    }

    [Fact]
    public void A_row_that_named_nothing_cannot_contradict_anything()
        => Assert.True(ImportService.NamesAgree(null, "Crocalor"));

    // -------------------------------------------------------------- through Apply

    [Fact]
    public void A_contradicted_row_is_put_in_front_of_you()
    {
        var row = new ImportRow { Index = 0, Source = "x", ClaimedName = "Gastly" };

        ImportService.Apply(row, Card("sv2-36", "Crocalor", "Paldea Evolved"));

        Assert.Equal(ImportStatus.Mismatch, row.Status);
        Assert.Contains("Gastly", row.Message);
        Assert.Contains("Crocalor", row.Message);
    }

    [Fact]
    public void An_agreeing_row_is_still_settled()
    {
        var row = new ImportRow { Index = 0, Source = "x", ClaimedName = "Gastly" };

        ImportService.Apply(row, Card("sm6-36", "Gastly", "Crimson Invasion"));

        Assert.Equal(ImportStatus.Matched, row.Status);
    }

    private static CardCandidate Card(string id, string name, string set)
        => new(id, name, set, "36", Rarity: null, ImageSmall: null,
               MarketPrice: null, Variants: ["normal"], PrintedTotal: null);
}
