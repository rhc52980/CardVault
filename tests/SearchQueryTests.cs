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

    [Theory]
    [InlineData("4/102", "number:4 set.printedTotal:102")]
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
    public void A_name_followed_by_a_number_uses_both(string input, string expected)
        => Assert.Equal(expected, SearchQuery.Build(input, null));

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
