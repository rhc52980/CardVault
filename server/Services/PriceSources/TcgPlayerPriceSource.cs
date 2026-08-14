using System.Text.Json;

namespace CardVault.Services.PriceSources;

/// <summary>
/// TCGplayer prices, as relayed inside the pokemontcg.io card payload.
///
/// No network call of its own: the catalogue already carries these, so reading
/// them here costs nothing and can't fail independently of the card fetch.
/// </summary>
public sealed class TcgPlayerPriceSource : IPriceSource
{
    public string Id => "tcgplayer";
    public string DisplayName => "TCGplayer";
    public string Currency => "USD";

    public bool CanPrice(string cardId, JsonElement card) => Pricing.AvailableVariants(card).Count > 0;

    public Task<IReadOnlyList<SourcedPrice>> GetPricesAsync(string cardId, JsonElement card, CancellationToken ct)
    {
        var prices = Pricing.AvailableVariants(card)
            .Select(variant =>
            {
                var p = Pricing.ForVariant(card, variant);
                return new SourcedPrice(variant, p.Market, p.Low, p.Mid, p.High);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<SourcedPrice>>(prices);
    }
}
