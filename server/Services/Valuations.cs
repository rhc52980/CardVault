namespace CardVault.Services;

/// <summary>
/// Prices you set yourself, kept alongside the ones fetched from a market.
///
/// Sealed product and slabs are the reason this exists. Nothing external prices them
/// reliably — eBay gives asking prices and only when it's configured — so for most
/// people the only figure a booster box ever has is the one they typed. Recording
/// each revaluation turns that from a number into a history: what you thought it was
/// worth in March, and what you think now.
///
/// Not an <see cref="PriceSources.IPriceSource"/>, deliberately. Those fetch from
/// somewhere and can be chosen as the market that drives valuation; this is your own
/// opinion and must never be mistaken for a market reading — see the exclusion in
/// <see cref="CollectionService"/>, which would otherwise quote your own number back
/// to you as the market price.
/// </summary>
public static class Valuations
{
    public const string Source = "manual";

    public const string SourceName = "Your valuation";
}
