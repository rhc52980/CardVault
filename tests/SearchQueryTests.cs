using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// The search box guesses what you meant from a few characters, so the rules need
/// pinning down — particularly the boundary between "this is a card number" and
/// "this is part of a card's name", which is easy to get subtly wrong.
/// </summary>
public class SearchQueryTests
{
    // ------------------------------------------------ numbers off a physical card

    [Theory]
    [InlineData("4", "number:4")]
    [InlineData("58", "number:58")]
    [InlineData("102", "number:102")]
    [InlineData("TG12", "number:TG12")]
    [InlineData("SV49", "number:SV49")]
    [InlineData("H5", "number:H5")]
    public void Bare_numbers_search_by_collector_number(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// Modern cards pad the collector number — "056/094" — but the catalogue stores
    /// it as "56", so typing what's on the card found nothing at all. The padding has
    /// to come off, and only where it's safe to remove it.
    /// </summary>
    [Theory]
    [InlineData("056", "number:56")]
    [InlineData("007", "number:7")]
    [InlineData("0004", "number:4")]
    public void Padded_numbers_lose_their_leading_zeros(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// Prefixed numbers keep their padding: "SWSH039" is stored with its zeros and
    /// "SWSH39" matches nothing, so trimming these would break the promos rather
    /// than fix them. Four-letter prefixes have to parse as numbers at all — at the
    /// old three-character cap they fell through to a name search that could never
    /// match a collector number.
    /// </summary>
    [Theory]
    [InlineData("SWSH039", "number:SWSH039")]
    [InlineData("HGSS01", "number:HGSS01")]
    [InlineData("SV049", "number:SV049")]
    [InlineData("TG01", "number:TG01")]
    [InlineData("XY01", "number:XY01")]
    public void Prefixed_numbers_keep_theirs(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    [Fact]
    public void A_number_that_is_all_zeros_stays_a_number()
        => Assert.Equal("number:0", SearchQuery.Build("000", null));

    [Theory]
    [InlineData("4/102", "number:4 set.printedTotal:102")]
    [InlineData("056/094", "number:56 set.printedTotal:94")]
    [InlineData("006/165", "number:6 set.printedTotal:165")]
    [InlineData("58/102", "number:58 set.printedTotal:102")]
    [InlineData("4 / 102", "number:4 set.printedTotal:102")]
    [InlineData("TG12/TG30", "number:TG12")] // non-numeric total can't filter on printedTotal
    public void Number_over_total_uses_the_denominator_to_pick_the_set(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// Secret rares are numbered past the printed total — "103/102". The set still
    /// has a printed total of 102, so the filter must not reject it.
    /// </summary>
    [Fact]
    public void Secret_rares_numbered_beyond_the_set_total_still_resolve()
        => Assert.Equal("number:103 set.printedTotal:102", SearchQuery.Build("103/102", null));

    // ------------------------------------------------------------ names vs numbers

    [Theory]
    [InlineData("charizard", "name:\"*charizard*\"")]
    [InlineData("Team Magma's Groudon", "name:\"*Team Magma's Groudon*\"")]
    public void Words_search_by_name(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// The trap: a name ending in a digit must not be read as a collector number.
    /// </summary>
    [Theory]
    [InlineData("Porygon2")]
    [InlineData("Rotom")]
    public void Names_ending_in_digits_are_not_mistaken_for_numbers(string input)
        => Assert.StartsWith("name:", SearchQuery.Build(input, null));

    [Theory]
    [InlineData("charizard 4", "name:\"*charizard*\" number:4")]
    [InlineData("charizard 4/102", "name:\"*charizard*\" number:4 set.printedTotal:102")]
    [InlineData("charizard 056/094", "name:\"*charizard*\" number:56 set.printedTotal:94")]
    [InlineData("iron valiant 089", "name:\"*iron valiant*\" number:89")]
    public void A_name_followed_by_a_number_uses_both(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// The slash is the slowest key in the sequence, and typing the number off the
    /// card is the quickest way to find it. A space or a hyphen means the same.
    /// Nothing is lost: both sides have to look like collector numbers for this to
    /// match at all, and a card name never does.
    /// </summary>
    [Theory]
    [InlineData("056 094", "number:56 set.printedTotal:94")]
    [InlineData("4 102", "number:4 set.printedTotal:102")]
    [InlineData("056-094", "number:56 set.printedTotal:94")]
    [InlineData("4 - 102", "number:4 set.printedTotal:102")]
    public void A_space_or_hyphen_separates_the_pair_like_a_slash(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    /// <summary>
    /// Typed with no separator at all. Ambiguous in principle — "45094" could be
    /// 45/094, 4/5094 or 450/94 — but a set total is a real quantity, so it must be
    /// at least ten, and no card is numbered above its own total. That leaves one
    /// reading standing for the shapes people actually type.
    /// </summary>
    [Theory]
    [InlineData("45094", "number:45 set.printedTotal:94")]
    [InlineData("045094", "number:45 set.printedTotal:94")]
    [InlineData("4102", "number:4 set.printedTotal:102")]
    [InlineData("56094", "number:56 set.printedTotal:94")]
    [InlineData("189198", "number:189 set.printedTotal:198")]
    public void A_run_of_digits_splits_into_number_and_total(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    [Fact]
    public void Where_two_readings_survive_the_three_digit_total_wins()
        // "1264" is both 1/264 and 12/64. Modern sets are overwhelmingly
        // three-digit, so that is the guess; type the slash for the other.
        => Assert.Equal("number:1 set.printedTotal:264", SearchQuery.Build("1264", null));

    [Theory]
    [InlineData("4", "number:4")]
    [InlineData("58", "number:58")]
    [InlineData("102", "number:102")]
    public void Short_numbers_are_never_split(string input, string expected)
        // Splitting "102" into 1/02 would be actively wrong — it is a card number.
        => Assert.Equal(expected, SearchQuery.Build(input, null));

    [Fact]
    public void A_run_that_cannot_be_a_pair_stays_a_number()
        // 0045: every split leaves a card numbered zero, so it is just a number.
        => Assert.Equal("number:45", SearchQuery.Build("0045", null));

    [Fact]
    public void A_name_followed_by_a_spaced_pair_still_splits_correctly()
        // The trap the word-by-word split exists for: taken at the last space, this
        // would be a card called "charizard 4" numbered 102.
        => Assert.Equal(
            "name:\"*charizard*\" number:4 set.printedTotal:102",
            SearchQuery.Build("charizard 4 102", null));

    [Fact]
    public void A_two_word_name_with_one_number_is_not_read_as_a_pair()
        // "valiant 89" cannot be a pair — "valiant" has no digits in it.
        => Assert.Equal(
            "name:\"*iron valiant*\" number:89",
            SearchQuery.Build("iron valiant 89", null));

    /// <summary>
    /// "Charizard V" and "Charizard ex" end in a word, not a number — the whole
    /// thing is the name.
    /// </summary>
    [Theory]
    [InlineData("Charizard V")]
    [InlineData("Charizard ex")]
    public void Suffixes_that_are_not_numbers_stay_part_of_the_name(string input)
        => Assert.Equal($"name:\"*{input}*\"", SearchQuery.Build(input, null));

    // ------------------------------------------------------------- passthrough

    [Theory]
    [InlineData("number:4")]
    [InlineData("rarity:\"Rare Holo\" types:Fire")]
    [InlineData("set.id:base1 number:4")]
    public void Raw_api_syntax_is_passed_through_untouched(string input)
        => Assert.Equal(input, SearchQuery.Build(input, null));

    // ------------------------------------------------------------- set filter

    [Fact]
    public void The_set_dropdown_is_combined_with_whatever_was_typed()
        => Assert.Equal("name:\"*charizard*\" set.id:base1", SearchQuery.Build("charizard", "base1"));

    [Fact]
    public void The_set_dropdown_works_on_its_own()
        => Assert.Equal("set.id:base1", SearchQuery.Build(null, "base1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_and_no_set_means_no_query(string? input)
        => Assert.Null(SearchQuery.Build(input, null));

    // ---------------------------------------------------------------- injection

    /// <summary>
    /// Quotes and brackets would otherwise let typed text break out of the query
    /// we're constructing and change its meaning.
    /// </summary>
    [Fact]
    public void Quotes_and_brackets_are_stripped_from_typed_text()
    {
        var built = SearchQuery.Build("chari\"zard (x)", null);
        Assert.Equal("name:\"*charizard x*\"", built);
    }
}
