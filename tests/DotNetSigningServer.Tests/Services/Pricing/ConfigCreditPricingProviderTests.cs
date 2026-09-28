using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Tests.Services.Pricing;

public class ConfigCreditPricingProviderTests
{
    private static decimal Amount(int documents, decimal? pricePer100 = null, BillingOptions? options = null)
    {
        var o = options ?? new BillingOptions();
        return ConfigCreditPricingProvider.CalculateAmount(documents, pricePer100 ?? o.PricePer100, o);
    }

    // The formula's cases (moved from BillingServiceTests, which keep covering the facade).
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 5)]
    [InlineData(100, 5)]
    [InlineData(101, 10)]
    [InlineData(200, 10)]
    [InlineData(299, 15)]
    [InlineData(300, 14.25)]
    [InlineData(400, 19)]
    [InlineData(499, 23.75)]
    [InlineData(500, 22.50)]
    [InlineData(999, 45)]
    [InlineData(1000, 42.50)]
    [InlineData(5000, 212.50)]
    public void CalculateAmount_DefaultOptions(int documents, double expected)
    {
        Assert.Equal((decimal)expected, Amount(documents));
    }

    [Fact]
    public void CalculateAmount_NonPositivePrice_IsZero()
    {
        Assert.Equal(0m, Amount(100, 0m));
        Assert.Equal(0m, Amount(100, -5m));
    }

    [Fact]
    public void CalculateAmount_RoundsAwayFromZero()
    {
        Assert.Equal(19.95m, Amount(300, options: new BillingOptions { PricePer100 = 7m }));
    }

    [Fact]
    public void Packs_AreTheSoldQuantitiesAtTheConfiguredPrices()
    {
        var sut = new ConfigCreditPricingProvider(new BillingOptions());

        var packs = sut.GetPacks();

        Assert.Equal(new[] { 100, 300, 500, 1000 }, packs.Select(p => p.Quantity));
        // Same cents as the imported price list of the service.
        Assert.Equal(new long[] { 500, 1425, 2250, 4250 }, packs.Select(p => p.UnitAmountMinor));
        Assert.All(packs, p =>
        {
            Assert.Equal("EUR", p.Currency);
            Assert.Null(p.LookupKey);
            Assert.Null(p.StripePriceId);
        });
        Assert.Equal(5m, sut.PricePer100);
        Assert.Equal("EUR", sut.Currency);
        Assert.Equal(CreditPricing.SourceConfig, sut.Source);
    }

    [Fact]
    public void GetPack_OfAnUnsoldQuantity_IsNull()
    {
        var sut = new ConfigCreditPricingProvider(new BillingOptions());

        Assert.Null(sut.GetPack(200));
        Assert.Equal(1425, sut.GetPack(300)!.UnitAmountMinor);
    }

    [Fact]
    public void Discounts_AreDerivedFromThePrices()
    {
        var sut = new ConfigCreditPricingProvider(new BillingOptions());

        Assert.Equal(new[] { 0, 5, 10, 15 }, sut.GetPacks().Select(p => p.DiscountPercentVersus(sut.PricePer100)));
    }

    [Fact]
    public void Currency_IsNormalised()
    {
        var sut = new ConfigCreditPricingProvider(new BillingOptions { Currency = " eur " });

        Assert.Equal("EUR", sut.Currency);
        Assert.Equal("eur", sut.GetPack(100)!.StripeCurrency);
    }

    [Fact]
    public void FormatAmount_UsesTheGivenCulture()
    {
        var pack = new ConfigCreditPricingProvider(new BillingOptions()).GetPack(300)!;

        Assert.Equal("14.25", pack.FormatAmount(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("14,25", pack.FormatAmount(new System.Globalization.CultureInfo("cs-CZ")));
        Assert.Equal("5", CreditPricing.FormatAmount(5m, System.Globalization.CultureInfo.InvariantCulture));
    }
}
