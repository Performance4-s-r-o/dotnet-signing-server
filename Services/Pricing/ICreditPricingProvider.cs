namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Current credit prices. The single place pages, checkout, auto-recharge and the price-change
/// monitor read prices from.
///
/// Implementations never call the P4 Backoffice service: they answer from configuration or
/// from the price-list snapshot kept in memory (and in <c>BackofficeState</c>), so they are
/// safe on request paths. Chosen by <c>P4Backoffice:Modules:Pricing</c>: On uses
/// <see cref="BackofficeCreditPricingProvider"/>, Off and Shadow <see cref="ConfigCreditPricingProvider"/>.
/// </summary>
public interface ICreditPricingProvider
{
    /// <summary>Every sold pack (<see cref="CreditPricing.Quantities"/>), smallest first.</summary>
    IReadOnlyList<CreditPack> GetPacks();

    /// <summary>The pack of <paramref name="quantity"/> credits; null when that pack is not sold.</summary>
    CreditPack? GetPack(int quantity);

    /// <summary>Price of 100 credits (the 100-pack), in major units.</summary>
    decimal PricePer100 { get; }

    /// <summary>ISO 4217 code, upper case.</summary>
    string Currency { get; }

    /// <summary><c>backoffice</c> when prices come from the service's price list, otherwise <c>config</c>.</summary>
    string Source { get; }
}
