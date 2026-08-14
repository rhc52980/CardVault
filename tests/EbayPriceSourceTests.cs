using System.Text.Json;
using CardVault.Services.PriceSources;

namespace CardVault.Tests;

/// <summary>
/// eBay gives us asking prices from a keyword search, which is the loosest data in
/// the app: the results contain things that aren't the item, and the prices contain
/// listings nobody will ever buy. These rules are what turns that into a number, so
/// they're worth pinning down — getting them wrong doesn't throw, it just quietly
/// values a sealed booster box at the price of an empty wrapper.
/// </summary>
public class EbayPriceSourceTests
{
    // ------------------------------------------------------------ the headline price

    [Fact]
    public void Headline_ignores_the_expensive_tail()
    {
        // Four realistic listings and two chancers. A median of all six sits at
        // 112.50 and would keep climbing as more optimists list; the cheapest band
        // ignores them entirely.
        double[] prices = [100, 105, 110, 115, 400, 900];

        Assert.Equal(105, EbayPriceSource.Headline(prices));
    }

    [Fact]
    public void Headline_of_a_tight_market_lands_inside_it()
    {
        double[] prices = [48, 50, 52];
        var headline = EbayPriceSource.Headline(prices);

        Assert.InRange(headline, 48, 52);
    }

    [Fact]
    public void Headline_never_takes_fewer_than_the_minimum_sample()
    {
        // A third of four is one listing, which would make the single cheapest
        // listing the price. The floor keeps at least three in play.
        double[] prices = [10, 90, 95, 100];

        Assert.Equal(90, EbayPriceSource.Headline(prices));
    }

    [Theory]
    [InlineData(new double[] { 10, 20, 30 }, 20)]
    [InlineData(new double[] { 10, 20, 30, 40 }, 25)]
    [InlineData(new double[] { 7 }, 7)]
    public void Median_handles_both_odd_and_even_counts(double[] prices, double expected)
        => Assert.Equal(expected, EbayPriceSource.Median(prices));

    // ------------------------------------------------------- keeping the wrong things out

    [Fact]
    public void A_listing_missing_a_word_from_the_name_is_not_a_match()
    {
        // The cheap trap: a single card, or the box's contents, when we asked for
        // a sealed box. Far cheaper, and it would drag the headline down hard.
        Assert.False(EbayPriceSource.Matches(
            "Evolving Skies Booster Box",
            "Pokemon Evolving Skies Booster Pack Single"));
    }

    [Fact]
    public void A_listing_carrying_every_word_matches_however_padded()
    {
        Assert.True(EbayPriceSource.Matches(
            "Evolving Skies Booster Box",
            "Pokemon TCG Evolving Skies Booster Box Factory Sealed 36 Packs IN HAND"));
    }

    [Fact]
    public void Matching_ignores_case_and_punctuation()
    {
        Assert.True(EbayPriceSource.Matches(
            "Charizard VMAX - Rainbow Rare",
            "charizard vmax rainbow rare psa 9"));
    }

    [Fact]
    public void Single_characters_are_not_required_to_appear()
    {
        // "1st Edition Base Set" splits to a bare "1st"; a lone letter or digit
        // elsewhere in a name shouldn't be able to veto every real listing.
        Assert.True(EbayPriceSource.Matches(
            "Base Set 2 Booster Box",
            "Pokemon Base Set 2 Booster Box Sealed"));
    }

    [Fact]
    public void An_empty_title_never_matches()
        => Assert.False(EbayPriceSource.Matches("Evolving Skies Booster Box", ""));

    // ------------------------------------------------------------------ the search query

    [Fact]
    public void The_query_is_the_items_own_name()
    {
        var card = Card("""{ "id": "custom-abc", "name": "Evolving Skies Booster Box", "supertype": "Sealed" }""");

        Assert.Equal("Evolving Skies Booster Box", EbayPriceSource.QueryFor(card));
    }

    [Fact]
    public void The_category_is_left_out_of_the_query()
    {
        // "Sealed" and "Custom" are our own filing, not words sellers put in titles.
        var card = Card("""{ "id": "custom-abc", "name": "Team Rocket ETB", "supertype": "Custom" }""");

        Assert.DoesNotContain("Custom", EbayPriceSource.QueryFor(card));
    }

    [Theory]
    [InlineData("""{ "id": "custom-abc" }""")]
    [InlineData("""{ "id": "custom-abc", "name": "" }""")]
    [InlineData("""{ "id": "custom-abc", "name": "   " }""")]
    public void An_item_with_no_usable_name_is_not_searched_for(string json)
        => Assert.Null(EbayPriceSource.QueryFor(Card(json)));

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
