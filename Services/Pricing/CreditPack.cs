using System.Globalization;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// A credit pack the product sells (100, 300, 500 or 1000 credits) at its current price.
/// </summary>
/// <param name="Quantity">Credits granted by one purchase.</param>
/// <param name="UnitAmountMinor">Price of the whole pack in minor units (cents).</param>
/// <param name="Currency">ISO 4217 code, upper case (<c>EUR</c>).</param>
/// <param name="LookupKey">Stripe Price lookup key from the price list; null for the configured price.</param>
/// <param name="StripePriceId">Stripe Price id the service reported for this environment; informational only.</param>
public sealed record CreditPack(int Quantity, long UnitAmountMinor, string Currency, string? LookupKey, string? StripePriceId)
{
    /// <summary>Minor units per major unit. Every currency the product sells in has two decimals.</summary>
    public const int MinorUnitsPerMajor = 100;

    /// <summary>The price in major units (e.g. 14.25).</summary>
    public decimal Amount => UnitAmountMinor / (decimal)MinorUnitsPerMajor;

    /// <summary>Currency as Stripe expects it (lower case).</summary>
    public string StripeCurrency => Currency.ToLowerInvariant();

    /// <summary>
    /// Discount against buying the same number of credits as 100-packs at <paramref name="pricePer100"/>,
    /// in whole percent (0 when there is none).
    /// </summary>
    public int DiscountPercentVersus(decimal pricePer100)
    {
        if (pricePer100 <= 0 || Quantity <= 0) return 0;
        var full = pricePer100 * Quantity / 100m;
        if (full <= 0 || Amount >= full) return 0;
        return (int)Math.Round((1 - Amount / full) * 100m, MidpointRounding.AwayFromZero);
    }

    /// <summary>Amount as shown on the pricing and billing pages (<c>14.25</c>, <c>5</c>).</summary>
    public string FormatAmount(IFormatProvider? culture = null) =>
        CreditPricing.FormatAmount(Amount, culture);
}

/// <summary>Things every pricing source shares.</summary>
public static class CreditPricing
{
    /// <summary>The packs the product sells, smallest first. Checkout accepts nothing else.</summary>
    public static readonly IReadOnlyList<int> Quantities = [100, 300, 500, 1000];

    /// <summary><see cref="ICreditPricingProvider.Source"/> of the service's price list.</summary>
    public const string SourceBackoffice = "backoffice";

    /// <summary><see cref="ICreditPricingProvider.Source"/> of <c>Billing:*</c> in appsettings.</summary>
    public const string SourceConfig = "config";

    public static bool IsSoldQuantity(int quantity) => Quantities.Contains(quantity);

    /// <summary>Formats a price the way the pages always did (<c>0.##</c>), in the UI culture by default.</summary>
    public static string FormatAmount(decimal amount, IFormatProvider? culture = null) =>
        amount.ToString("0.##", culture ?? CultureInfo.CurrentUICulture);

    /// <summary>Major units (e.g. 14.25) to minor units, rounded like the checkout always did.</summary>
    public static long ToMinorUnits(decimal amount) =>
        (long)Math.Round(amount * CreditPack.MinorUnitsPerMajor, MidpointRounding.AwayFromZero);
}
