namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Current credit prices. The single place pages, checkout, auto-recharge and the price-change
/// notices read prices from.
///
/// Prices and their currency come only from the service's price list (#175): there is no
/// configured price list and no default currency. Implementations never call the P4 Backoffice
/// service: they answer from the price-list snapshot kept in memory (and in
/// <c>BackofficeState</c>), so they are safe on request paths. Chosen by
/// <c>P4Backoffice:Modules:Pricing</c>: On uses <see cref="BackofficeCreditPricingProvider"/>,
/// Off <see cref="NoCreditPricingProvider"/>.
///
/// An empty price list (Pricing Off, on-prem, or no snapshot yet) means nothing is sold: no
/// pack, no price and no currency; pages then show no price at all, never a price of 0.
/// </summary>
public interface ICreditPricingProvider
{
    /// <summary>Every sold pack (<see cref="CreditPricing.Quantities"/>), smallest first; empty when nothing is sold.</summary>
    IReadOnlyList<CreditPack> GetPacks();

    /// <summary>The pack of <paramref name="quantity"/> credits; null when that pack is not sold.</summary>
    CreditPack? GetPack(int quantity);

    /// <summary>Price of 100 credits (the 100-pack), in major units; null when the 100-pack is not sold.</summary>
    decimal? PricePer100 { get; }

    /// <summary>ISO 4217 code of the price list, upper case; null when nothing is sold.</summary>
    string? Currency { get; }
}

/// <summary>Pricing Off (on-prem, development): no price list, so nothing is sold and no price is shown.</summary>
public sealed class NoCreditPricingProvider : ICreditPricingProvider
{
    public IReadOnlyList<CreditPack> GetPacks() => [];

    public CreditPack? GetPack(int quantity) => null;

    public decimal? PricePer100 => null;

    public string? Currency => null;
}
