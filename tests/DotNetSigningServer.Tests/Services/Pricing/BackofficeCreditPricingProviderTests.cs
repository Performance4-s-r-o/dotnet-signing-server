using DotNetSigningServer.Services.Pricing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Pricing;

public class BackofficeCreditPricingProviderTests
{
    private static (BackofficeCreditPricingProvider Sut, PricingSnapshotHolder Holder) Create()
    {
        var holder = new PricingSnapshotHolder();
        var sut = new BackofficeCreditPricingProvider(holder, NullLogger<BackofficeCreditPricingProvider>.Instance);
        return (sut, holder);
    }

    /// <summary>An item priced in two currencies.</summary>
    private static Dictionary<string, object?> TwoCurrencies(int quantity, long eur, long czk)
    {
        var item = PricingTestData.Item(quantity, eur);
        var other = (object[])PricingTestData.Item(quantity, czk, currency: "czk")["prices"]!;
        item["prices"] = ((object[])item["prices"]!).Concat(other).ToArray();
        return item;
    }

    [Fact]
    public void WithoutSnapshot_NothingIsSold()
    {
        var (sut, _) = Create();

        Assert.Empty(sut.GetPacks());
        Assert.Null(sut.GetPack(100));
        Assert.Null(sut.PricePer100);
        Assert.Null(sut.Currency);
    }

    [Fact]
    public void EmptyPriceList_NothingIsSold()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1)));

        Assert.Empty(sut.GetPacks());
        Assert.Null(sut.PricePer100);
        Assert.Null(sut.Currency);
    }

    [Fact]
    public void PricingOff_SellsNothing()
    {
        var sut = new NoCreditPricingProvider();

        Assert.Empty(sut.GetPacks());
        Assert.Null(sut.GetPack(100));
        Assert.Null(sut.PricePer100);
        Assert.Null(sut.Currency);
    }

    [Fact]
    public void Snapshot_MapsCreditItemsToPacks()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.ImportedJson(amounts: new Dictionary<int, long> { [100] = 600, [300] = 1500 })));

        var packs = sut.GetPacks();

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
    public void MissingItem_IsNotSold()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Item(100, 500), PricingTestData.Item(500, 2250), PricingTestData.Item(1000, 4250))));

        Assert.Null(sut.GetPack(300));
        Assert.Equal(new[] { 100, 500, 1000 }, sut.GetPacks().Select(p => p.Quantity));
        Assert.Equal("pd_credits_500_once_eur", sut.GetPack(500)!.LookupKey);
    }

    [Fact]
    public void Without100Pack_NoPricePer100()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1, PricingTestData.Item(300, 1425))));

        Assert.Null(sut.PricePer100);
        Assert.Equal("EUR", sut.Currency);
    }

    [Fact]
    public void Currency_ComesFromThePriceList()
    {
        var (sut, holder) = Create();
        holder.Set(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Imported.Select(p => PricingTestData.Item(p.Quantity, p.Cents * 25, currency: "czk")).ToArray())));

        Assert.Equal(12500, sut.GetPack(100)!.UnitAmountMinor);
        Assert.Equal("CZK", sut.Currency);
        Assert.All(sut.GetPacks(), p => Assert.Equal("CZK", p.Currency));
    }

    [Fact]
    public void SeveralCurrencies_TheOnePricingMostPacksWins()
    {
        var mapping = PriceListMapper.Map(PricingTestData.Snapshot(PricingTestData.Json(1,
            TwoCurrencies(100, 500, 12500),
            TwoCurrencies(300, 1425, 35000),
            PricingTestData.Item(500, 56000, currency: "czk"))).Body);

        Assert.Equal("CZK", mapping.Currency);
        Assert.Equal(new[] { 100, 300, 500 }, mapping.Packs.Keys.Order());
        Assert.Contains(mapping.Problems, p => p.Contains("CZK, EUR"));
    }

    [Fact]
    public void SeveralCurrencies_TieGoesToThe100Pack()
    {
        var mapping = PriceListMapper.Map(PricingTestData.Snapshot(PricingTestData.Json(1,
            PricingTestData.Item(100, 12500, currency: "czk"),
            PricingTestData.Item(300, 1425))).Body);

        Assert.Equal("CZK", mapping.Currency);
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
