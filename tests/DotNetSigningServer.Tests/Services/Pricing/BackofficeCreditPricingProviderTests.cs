using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Pricing;

public class BackofficeCreditPricingProviderTests
{
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static (BackofficeCreditPricingProvider Sut, PricingSnapshotHolder Holder) Create(
        BillingOptions? billing = null, string stripeCurrency = "EUR")
    {
        var holder = new PricingSnapshotHolder();
        var sut = new BackofficeCreditPricingProvider(
            holder,
            new ConfigCreditPricingProvider(billing ?? new BillingOptions()),
            new Monitor<StripeOptions>(new StripeOptions { Currency = stripeCurrency }),
            NullLogger<BackofficeCreditPricingProvider>.Instance);
        return (sut, holder);
    }

    [Fact]
    public void WithoutSnapshot_UsesTheConfiguredPrices()
    {
        var (sut, _) = Create(new BillingOptions { PricePer100 = 6m });

        Assert.Equal(CreditPricing.SourceConfig, sut.Source);
        Assert.Equal(600, sut.GetPack(100)!.UnitAmountMinor);
        Assert.Null(sut.GetPack(100)!.LookupKey);
        Assert.Equal(6m, sut.PricePer100);
    }

    [Fact]
    public void Snapshot_MapsCreditItemsToPacks()
    {
        var (sut, holder) = Create(new BillingOptions { PricePer100 = 99m });
        holder.Set(PricingTestData.Snapshot(PricingTestData.ImportedJson(amounts: new Dictionary<int, long> { [100] = 600, [300] = 1500 })));

        var packs = sut.GetPacks();

        Assert.Equal(CreditPricing.SourceBackoffice, sut.Source);
        Assert.Equal(new[] { 100, 300, 500, 1000 }, packs.Select(p => p.Quantity));
        Assert.Equal(new long[] { 600, 1500, 2250, 4250 }, packs.Select(p => p.UnitAmountMinor));
        var pack300 = sut.GetPack(300)!;
        Assert.Equal("pd_credits_300_once_eur", pack300.LookupKey);
        Assert.Equal("price_300", pack300.StripePriceId);
        Assert.Equal("EUR", pack300.Currency);
        Assert.Equal(6m, sut.PricePer100);
        Assert.Equal("EUR", sut.Currency);
    }

    [Fact]
    public void MissingItem_FallsBackToTheConfiguredPriceOfThatPackOnly()
    {
        var (sut, holder) = Create(new BillingOptions { PricePer100 = 7m });
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Item(100, 500), PricingTestData.Item(500, 2250), PricingTestData.Item(1000, 4250))));

        var pack300 = sut.GetPack(300)!;

        Assert.Equal(1995, pack300.UnitAmountMinor); // 3 × 7 − 5 %
        Assert.Null(pack300.LookupKey);
        Assert.Equal("pd_credits_500_once_eur", sut.GetPack(500)!.LookupKey);
        Assert.Equal(CreditPricing.SourceBackoffice, sut.Source);
    }

    [Fact]
    public void OtherCurrency_UsesTheConfiguredPrices()
    {
        var (sut, holder) = Create(stripeCurrency: "EUR");
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Imported.Select(p => PricingTestData.Item(p.Quantity, p.Cents * 25, currency: "czk")).ToArray())));

        Assert.Equal(CreditPricing.SourceConfig, sut.Source);
        Assert.All(sut.GetPacks(), p => Assert.Null(p.LookupKey));
        Assert.Equal(500, sut.GetPack(100)!.UnitAmountMinor);
    }

    [Fact]
    public void StripeCurrency_SelectsThePrice()
    {
        var (sut, holder) = Create(stripeCurrency: "czk");
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Imported.Select(p => PricingTestData.Item(p.Quantity, p.Cents * 25, currency: "czk")).ToArray())));

        Assert.Equal(12500, sut.GetPack(100)!.UnitAmountMinor);
        Assert.Equal("CZK", sut.Currency);
    }

    [Fact]
    public void RecurringTieredAndOtherKinds_AreIgnored()
    {
        var mapping = PriceListMapper.Map(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Item(100, 500, interval: "month"),
            PricingTestData.Item(300, 1425, tiersMode: "volume"),
            PricingTestData.Item(500, 2250, kind: "addon"),
            PricingTestData.Item(1000, 4250, quantityValue: "1000"),
            PricingTestData.Item(200, 900))).Body, "EUR");

        Assert.Equal(new[] { 1000 }, mapping.Packs.Keys);
        Assert.Contains(mapping.Problems, p => p.Contains("credits_100"));
        Assert.Contains(mapping.Problems, p => p.Contains("credits_300"));
        Assert.Contains(mapping.Problems, p => p.Contains("500 credits"));
    }

    [Fact]
    public void NewSnapshot_IsPickedUp()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.ImportedJson(1)));
        Assert.Equal(500, sut.GetPack(100)!.UnitAmountMinor);

        holder.Set(PricingTestData.Snapshot(PricingTestData.ImportedJson(2, new Dictionary<int, long> { [100] = 550 })));

        Assert.Equal(550, sut.GetPack(100)!.UnitAmountMinor);
    }

    [Fact]
    public void StoredSnapshot_RoundTrips()
    {
        var snapshot = PricingTestData.Snapshot(PricingTestData.ImportedJson(3), "\"etag-3\"");

        var copy = PricingSnapshot.TryDeserialize(snapshot.Serialize());

        Assert.NotNull(copy);
        Assert.Equal(3, copy!.Version);
        Assert.Equal("\"etag-3\"", copy.ETag);
        Assert.Equal(4, PriceListMapper.Map(copy.Body, "EUR").Packs.Count);
        Assert.Null(PricingSnapshot.TryDeserialize("not json"));
        Assert.Null(PricingSnapshot.TryDeserialize(""));
    }
}
