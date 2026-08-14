using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// The search box now feeds two very different things — a pokemontcg.io query and
/// SQL against the offline catalogue — from one parse. These pin down that the two
/// stay in step, because a card that the API finds and the local copy doesn't is a
/// bug you'd blame on the download rather than on the query.
/// </summary>
public class CatalogueSearchTests
{
    // ------------------------------------------------------------------ parsing

    [Fact]
    public void A_padded_number_pair_parses_to_number_and_printed_total()
    {
        var intent = SearchQuery.Parse("056/094", null);

        Assert.Null(intent.Name);
        Assert.Equal("56", intent.Number);
        Assert.Equal(94, intent.PrintedTotal);
    }

    [Fact]
    public void A_name_and_number_parse_apart()
    {
        var intent = SearchQuery.Parse("charizard 4/102", null);

        Assert.Equal("charizard", intent.Name);
        Assert.Equal("4", intent.Number);
        Assert.Equal(102, intent.PrintedTotal);
    }

    [Fact]
    public void A_non_numeric_denominator_yields_no_printed_total()
        // "SV49/SV94" — nothing can filter on a denominator that isn't a number.
        => Assert.Null(SearchQuery.Parse("SV49/SV94", null).PrintedTotal);

    // -------------------------------------------------------- what can run locally

    [Fact]
    public void Api_field_syntax_cannot_run_locally()
    {
        // The catalogue can't honour arbitrary API fields, so these must stay with
        // the API rather than quietly returning nothing.
        var intent = SearchQuery.Parse("rarity:\"Rare Holo\" types:Fire", null);

        Assert.False(intent.CanRunLocally);
        Assert.Null(SearchQuery.ToSql(intent));
    }

    [Fact]
    public void An_empty_search_cannot_run_locally()
        => Assert.False(SearchQuery.Parse("", null).CanRunLocally);

    [Fact]
    public void A_set_filter_on_its_own_can_run_locally()
        => Assert.True(SearchQuery.Parse("", "base1").CanRunLocally);

    // ------------------------------------------------------------------ rendering

    [Fact]
    public void A_number_pair_becomes_an_exact_match_on_both_columns()
    {
        var sql = SearchQuery.ToSql(SearchQuery.Parse("056/094", null));
        Assert.NotNull(sql);

        var (where, parameters) = sql!.Value;

        // Exact, not LIKE: "4" must not also match "14" or "40".
        Assert.Contains("number = $number", where);
        Assert.Contains("printed_total = $printedTotal", where);
        Assert.Equal("56", parameters["$number"]);
        Assert.Equal(94, parameters["$printedTotal"]);
    }

    [Fact]
    public void A_name_becomes_a_substring_match()
    {
        var (where, parameters) = SearchQuery.ToSql(SearchQuery.Parse("charizard", null))!.Value;

        Assert.Contains("name LIKE $name", where);
        Assert.Equal("%charizard%", parameters["$name"]);
    }

    [Fact]
    public void Like_wildcards_in_a_card_name_are_escaped()
    {
        // Without escaping, a name containing % would match every card in the
        // catalogue rather than the one that actually has it in its name.
        var (where, parameters) = SearchQuery.ToSql(SearchQuery.Parse("100% Pikachu", null))!.Value;

        Assert.Contains(@"ESCAPE '\'", where);
        Assert.Equal(@"%100\% Pikachu%", parameters["$name"]);
    }

    [Fact]
    public void Every_parsed_part_reaches_the_clause()
    {
        var (where, parameters) = SearchQuery.ToSql(SearchQuery.Parse("charizard 4/102", "base1"))!.Value;

        Assert.Contains("name LIKE", where);
        Assert.Contains("number =", where);
        Assert.Contains("printed_total =", where);
        Assert.Contains("set_id =", where);
        Assert.Equal(4, parameters.Count);
    }

    // ------------------------------------------------- the two paths agreeing

    [Theory]
    [InlineData("056/094")]
    [InlineData("SWSH039")]
    [InlineData("charizard 4/102")]
    [InlineData("iron valiant")]
    public void Anything_the_api_can_search_for_the_catalogue_can_too(string typed)
    {
        // Not comparing the rendered strings — they're different languages. The point
        // is that neither path silently drops a query the other would have answered.
        var intent = SearchQuery.Parse(typed, null);

        Assert.NotNull(SearchQuery.Build(typed, null));
        Assert.True(intent.CanRunLocally);
        Assert.NotNull(SearchQuery.ToSql(intent));
    }
}

/// <summary>
/// Printings for a card added while offline. The catalogue has no price block, and
/// the price block is where printings normally come from, so these are guessed —
/// which makes it worth pinning down that the guesses are at least possible cards.
/// </summary>
public class LikelyVariantsTests
{
    [Fact]
    public void A_modern_common_comes_in_normal_and_reverse()
        => Assert.Equal(["normal", "reverseHolofoil"], Pricing.LikelyVariants("Common", "2023/06/30"));

    [Fact]
    public void A_modern_holo_rare_comes_in_holo_and_reverse()
        => Assert.Equal(["holofoil", "reverseHolofoil"], Pricing.LikelyVariants("Rare Holo", "2023/06/30"));

    [Fact]
    public void An_sv_era_ex_is_foil_only_never_normal()
        // "Double Rare" is the Scarlet & Violet name for an ex, which has no plain
        // printing at all — offering "normal" would offer a card that doesn't exist.
        => Assert.DoesNotContain("normal", Pricing.LikelyVariants("Double Rare", "2024/01/26"));

    [Theory]
    [InlineData("Illustration Rare")]
    [InlineData("Special Illustration Rare")]
    [InlineData("Hyper Rare")]
    [InlineData("Ultra Rare")]
    [InlineData("Secret Rare")]
    [InlineData("Radiant Rare")]
    public void The_foil_only_rarities_are_never_offered_as_normal(string rarity)
        => Assert.DoesNotContain("normal", Pricing.LikelyVariants(rarity, "2023/06/30"));

    [Fact]
    public void A_base_set_card_is_never_offered_a_reverse_holo()
    {
        // Reverse holos arrive with Legendary Collection in 2002. Offering one for a
        // 1999 card would be offering a printing that has never existed.
        Assert.Equal(["normal"], Pricing.LikelyVariants("Common", "1999/01/09"));
        Assert.Equal(["holofoil"], Pricing.LikelyVariants("Rare Holo", "1999/01/09"));
    }

    [Fact]
    public void An_unknown_rarity_or_date_still_offers_something_addable()
    {
        // A card is being added right now; an empty dropdown is not an option.
        Assert.NotEmpty(Pricing.LikelyVariants(null, null));
        Assert.NotEmpty(Pricing.LikelyVariants("Who knows", "not a date"));
    }
}
