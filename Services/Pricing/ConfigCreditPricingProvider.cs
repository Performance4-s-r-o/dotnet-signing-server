using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Prices from <c>Billing:*</c> (<see cref="BillingOptions"/>): <c>PricePer100</c> per started
/// hundred credits, minus <c>Discount300</c> / <c>Discount500</c> / <c>Discount1000</c>.
/// The only implementation of that formula; the fallback of <see cref="BackofficeCreditPricingProvider"/>.
/// </summary>
public sealed class ConfigCreditPricingProvider : ICreditPricingProvider
{
    private readonly IOptionsMonitor<BillingOptions>? _monitor;
    private readonly BillingOptions? _fixed;

    public ConfigCreditPricingProvider(IOptionsMonitor<BillingOptions> options)
    {
        _monitor = options;
    }

    /// <summary>Fixed options (tests, one-off calculations).</summary>
    public ConfigCreditPricingProvider(BillingOptions options)
    {
        _fixed = options;
    }

    private BillingOptions Options => _fixed ?? _monitor!.CurrentValue;

    public string Source => CreditPricing.SourceConfig;

    public decimal PricePer100 => Options.PricePer100;

    public string Currency => NormalizeCurrency(Options.Currency);

    public IReadOnlyList<CreditPack> GetPacks()
    {
        var options = Options;
        return CreditPricing.Quantities.Select(q => Pack(q, options)).ToList();
    }

    public CreditPack? GetPack(int quantity) =>
        CreditPricing.IsSoldQuantity(quantity) ? Pack(quantity, Options) : null;

    private static CreditPack Pack(int quantity, BillingOptions options) =>
        new(quantity,
            CreditPricing.ToMinorUnits(CalculateAmount(quantity, options.PricePer100, options)),
            NormalizeCurrency(options.Currency),
            LookupKey: null,
            StripePriceId: null);

    /// <summary>
    /// Price of <paramref name="documentCount"/> credits at <paramref name="pricePer100"/>:
    /// every started hundred costs <paramref name="pricePer100"/>, then the volume discount of
    /// the largest tier reached (300 / 500 / 1000), rounded to cents away from zero.
    /// 0 for no credits or a non-positive price.
    /// </summary>
    public static decimal CalculateAmount(int documentCount, decimal pricePer100, BillingOptions options)
    {
        if (documentCount <= 0 || pricePer100 <= 0)
        {
            return 0m;
        }

        int units = (int)Math.Ceiling(documentCount / 100m);
        var amount = units * pricePer100;

        decimal discount = 0m;
        if (documentCount >= 1000)
        {
            discount = options.Discount1000;
        }
        else if (documentCount >= 500)
        {
            discount = options.Discount500;
        }
        else if (documentCount >= 300)
        {
            discount = options.Discount300;
        }

        if (discount > 0)
        {
            amount -= amount * discount;
        }

        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    internal static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
}
