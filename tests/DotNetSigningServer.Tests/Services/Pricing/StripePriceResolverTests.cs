using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;

namespace DotNetSigningServer.Tests.Services.Pricing;

public class StripePriceResolverTests
{
    private readonly Mock<IStripeClient> _stripe = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly List<PriceListOptions> _lists = new();

    private StripePriceResolver Create() => new(_stripe.Object, _time, NullLogger<StripePriceResolver>.Instance);

    private static Price Price(string id = "price_300", long amount = 1425, string currency = "eur",
        string lookupKey = "pd_credits_300_once_eur", bool active = true, string type = "one_time") => new()
        {
            Id = id,
            Active = active,
            Currency = currency,
            UnitAmount = amount,
            LookupKey = lookupKey,
            Type = type,
        };

    private void StripeReturns(params Price[] prices) =>
        _stripe.Setup(c => c.RequestAsync<StripeList<Price>>(
                HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, o, _, _) => _lists.Add((PriceListOptions)o))
            .ReturnsAsync(() => new StripeList<Price> { Data = prices.ToList() });

    [Fact]
    public async Task MatchingPrice_IsUsed()
    {
        StripeReturns(Price());

        var id = await Create().ResolveAsync(PricingTestData.Pack());

        Assert.Equal("price_300", id);
        var options = Assert.Single(_lists);
        Assert.Equal(new[] { "pd_credits_300_once_eur" }, options.LookupKeys);
        Assert.True(options.Active);
    }

    [Fact]
    public async Task Answers_AreCachedForTenMinutes()
    {
        StripeReturns(Price());
        var sut = Create();

        await sut.ResolveAsync(PricingTestData.Pack());
        _time.Advance(TimeSpan.FromMinutes(9));
        await sut.ResolveAsync(PricingTestData.Pack());
        Assert.Single(_lists);

        _time.Advance(TimeSpan.FromMinutes(2));
        await sut.ResolveAsync(PricingTestData.Pack());
        Assert.Equal(2, _lists.Count);
    }

    [Fact]
    public async Task Invalidate_DropsTheCache()
    {
        StripeReturns(Price());
        var sut = Create();
        await sut.ResolveAsync(PricingTestData.Pack());

        sut.Invalidate();
        await sut.ResolveAsync(PricingTestData.Pack());

        Assert.Equal(2, _lists.Count);
    }

    [Fact]
    public async Task AmountMismatch_GivesNull()
    {
        StripeReturns(Price(amount: 1500));

        Assert.Null(await Create().ResolveAsync(PricingTestData.Pack(cents: 1425)));
    }

    [Fact]
    public async Task CurrencyMismatch_GivesNull()
    {
        StripeReturns(Price(currency: "czk"));

        Assert.Null(await Create().ResolveAsync(PricingTestData.Pack()));
    }

    [Fact]
    public async Task RecurringOrInactivePrice_GivesNull()
    {
        StripeReturns(Price(type: "recurring"));
        Assert.Null(await Create().ResolveAsync(PricingTestData.Pack()));

        _lists.Clear();
        StripeReturns(Price(active: false));
        Assert.Null(await Create().ResolveAsync(PricingTestData.Pack()));
    }

    [Fact]
    public async Task NoPrice_GivesNullAndIsCached()
    {
        StripeReturns();
        var sut = Create();

        Assert.Null(await sut.ResolveAsync(PricingTestData.Pack()));
        Assert.Null(await sut.ResolveAsync(PricingTestData.Pack()));
        Assert.Single(_lists);
    }

    [Fact]
    public async Task StripeFailure_GivesNullAndIsNotCached()
    {
        _stripe.SetupSequence(c => c.RequestAsync<StripeList<Price>>(
                HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("boom"))
            .ReturnsAsync(new StripeList<Price> { Data = [Price()] });
        var sut = Create();

        Assert.Null(await sut.ResolveAsync(PricingTestData.Pack()));
        Assert.Equal("price_300", await sut.ResolveAsync(PricingTestData.Pack()));
    }

    [Fact]
    public async Task PackWithoutLookupKey_NeverCallsStripe()
    {
        Assert.Null(await Create().ResolveAsync(PricingTestData.Pack(lookupKey: null)));
        _stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithoutStripeClient_GivesNull()
    {
        var sut = new StripePriceResolver(null, _time, NullLogger<StripePriceResolver>.Instance);

        Assert.Null(await sut.ResolveAsync(PricingTestData.Pack()));
    }
}
