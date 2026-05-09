using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

public class BillingServiceTests
{
    private static BillingService CreateService(BillingOptions? options = null)
    {
        var opts = options ?? new BillingOptions();
        return new BillingService(TestHelpers.WrapOptions(opts));
    }

    [Fact]
    public void ZeroDocuments_ReturnsZero()
    {
        var sut = CreateService();
        Assert.Equal(0m, sut.CalculateAmountForDocuments(0));
    }

    [Fact]
    public void NegativeDocuments_ReturnsZero()
    {
        var sut = CreateService();
        Assert.Equal(0m, sut.CalculateAmountForDocuments(-5));
    }

    [Fact]
    public void OneDocument_ChargesOneUnit()
    {
        // 1 doc → ceil(1/100) = 1 unit → 1 * 5 = 5
        var sut = CreateService();
        Assert.Equal(5m, sut.CalculateAmountForDocuments(1));
    }

    [Fact]
    public void Exactly100Documents_ChargesOneUnit()
    {
        var sut = CreateService();
        Assert.Equal(5m, sut.CalculateAmountForDocuments(100));
    }

    [Fact]
    public void Documents101_ChargesTwoUnits()
    {
        // 101 docs → ceil(101/100) = 2 units → 2 * 5 = 10
        var sut = CreateService();
        Assert.Equal(10m, sut.CalculateAmountForDocuments(101));
    }

    [Fact]
    public void Documents200_NoDiscount()
    {
        // 200 docs → 2 units → 2 * 5 = 10, no discount (below 300)
        var sut = CreateService();
        Assert.Equal(10m, sut.CalculateAmountForDocuments(200));
    }

    [Fact]
    public void Documents300_Gets5PercentDiscount()
    {
        // 300 docs → 3 units → 3 * 5 = 15 → 5% discount → 15 - 0.75 = 14.25
        var sut = CreateService();
        Assert.Equal(14.25m, sut.CalculateAmountForDocuments(300));
    }

    [Fact]
    public void Documents400_Gets5PercentDiscount()
    {
        // 400 docs → 4 units → 4 * 5 = 20 → 5% discount → 20 - 1 = 19
        var sut = CreateService();
        Assert.Equal(19m, sut.CalculateAmountForDocuments(400));
    }

    [Fact]
    public void Documents500_Gets10PercentDiscount()
    {
        // 500 docs → 5 units → 5 * 5 = 25 → 10% discount → 25 - 2.50 = 22.50
        var sut = CreateService();
        Assert.Equal(22.50m, sut.CalculateAmountForDocuments(500));
    }

    [Fact]
    public void Documents999_Gets10PercentDiscount()
    {
        // 999 docs → 10 units → 10 * 5 = 50 → 10% discount → 50 - 5 = 45
        var sut = CreateService();
        Assert.Equal(45m, sut.CalculateAmountForDocuments(999));
    }

    [Fact]
    public void Documents1000_Gets15PercentDiscount()
    {
        // 1000 docs → 10 units → 10 * 5 = 50 → 15% discount → 50 - 7.50 = 42.50
        var sut = CreateService();
        Assert.Equal(42.50m, sut.CalculateAmountForDocuments(1000));
    }

    [Fact]
    public void Documents5000_Gets15PercentDiscount()
    {
        // 5000 docs → 50 units → 50 * 5 = 250 → 15% discount → 250 - 37.50 = 212.50
        var sut = CreateService();
        Assert.Equal(212.50m, sut.CalculateAmountForDocuments(5000));
    }

    [Fact]
    public void PriceOverride_IsUsed()
    {
        // 100 docs → 1 unit → 1 * 10 = 10 (overriding default 5)
        var sut = CreateService();
        Assert.Equal(10m, sut.CalculateAmountForDocuments(100, pricePer100Override: 10m));
    }

    [Fact]
    public void PriceOverride_ZeroPrice_ReturnsZero()
    {
        var sut = CreateService();
        Assert.Equal(0m, sut.CalculateAmountForDocuments(100, pricePer100Override: 0m));
    }

    [Fact]
    public void PriceOverride_NegativePrice_ReturnsZero()
    {
        var sut = CreateService();
        Assert.Equal(0m, sut.CalculateAmountForDocuments(100, pricePer100Override: -5m));
    }

    [Fact]
    public void InterfaceMethod_PassesThroughPriceOverride()
    {
        IBillingService sut = CreateService();
        // 100 docs → 1 unit → 1 * 8 = 8
        Assert.Equal(8m, sut.CalculateAmountForDocuments(100, 8m));
    }

    [Fact]
    public void RoundingApplied_AwayFromZero()
    {
        // Custom options: PricePer100 = 7, 300 docs → 3 units → 21 → 5% discount → 21 - 1.05 = 19.95
        var sut = CreateService(new BillingOptions { PricePer100 = 7m });
        Assert.Equal(19.95m, sut.CalculateAmountForDocuments(300));
    }

    [Fact]
    public void BetweenTiers_299Documents_NoDiscount()
    {
        // 299 docs → 3 units → 15, no discount (below 300)
        var sut = CreateService();
        Assert.Equal(15m, sut.CalculateAmountForDocuments(299));
    }

    [Fact]
    public void BetweenTiers_499Documents_Gets5PercentDiscount()
    {
        // 499 docs → 5 units → 25 → 5% discount → 23.75
        var sut = CreateService();
        Assert.Equal(23.75m, sut.CalculateAmountForDocuments(499));
    }
}
