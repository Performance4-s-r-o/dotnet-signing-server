using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Stripe.Checkout;

namespace DotNetSigningServer.Tests.Services.Pricing;

public class StripeCheckoutServiceTests
{
    private readonly Mock<IStripeClient> _stripe = new();
    private readonly List<SessionCreateOptions> _sessions = new();
    private readonly User _user = TestHelpers.CreateTestUser();

    private static readonly Dictionary<string, string> Metadata = new()
    {
        ["userId"] = "u1",
        ["documents"] = "300",
        ["autoRecharge"] = "False",
    };

    public StripeCheckoutServiceTests()
    {
        _user.StripeCustomerId = "cus_1";
    }

    private StripeCheckoutService Create(params Price[] prices)
    {
        _stripe.Setup(c => c.RequestAsync<StripeList<Price>>(
                HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<Price> { Data = prices.ToList() });
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var resolver = new StripePriceResolver(_stripe.Object, time, NullLogger<StripePriceResolver>.Instance);
        return new StripeCheckoutService(TestHelpers.WrapOptions(new StripeOptions()), resolver,
            NullLogger<StripeCheckoutService>.Instance, _stripe.Object);
    }

    private void SessionsAnswer(params Func<Session>[] answers)
    {
        var queue = new Queue<Func<Session>>(answers);
        _stripe.Setup(c => c.RequestAsync<Session>(
                HttpMethod.Post, It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, o, _, _) => _sessions.Add((SessionCreateOptions)o))
            .ReturnsAsync(() => queue.Dequeue()());
    }

    private static Price Price300(long amount = 1425) => new()
    {
        Id = "price_300", Active = true, Currency = "eur", UnitAmount = amount,
        LookupKey = "pd_credits_300_once_eur", Type = "one_time",
    };

    [Fact]
    public async Task PackWithMatchingStripePrice_ChargesThePrice()
    {
        var sut = Create(Price300());
        SessionsAnswer(() => new Session { Url = "https://checkout.test/1" });

        var url = await sut.CreateCheckoutSessionAsync(_user, PricingTestData.Pack(), "https://s", "https://c", Metadata, saveCard: true);

        Assert.Equal("https://checkout.test/1", url);
        var session = Assert.Single(_sessions);
        var line = Assert.Single(session.LineItems);
        Assert.Equal("price_300", line.Price);
        Assert.Equal(1, line.Quantity);
        Assert.Null(line.PriceData);
        // Everything else is as before; the webhook still reads metadata.documents.
        Assert.Equal("300", session.Metadata["documents"]);
        Assert.True(session.AutomaticTax.Enabled);
        Assert.True(session.TaxIdCollection.Enabled);
        Assert.True(session.InvoiceCreation.Enabled);
        Assert.Equal("off_session", session.PaymentIntentData.SetupFutureUsage);
        Assert.Equal("cus_1", session.Customer);
    }

    [Fact]
    public async Task AmountMismatch_ChargesThePackAmountInline()
    {
        var sut = Create(Price300(amount: 1500));
        SessionsAnswer(() => new Session { Url = "https://checkout.test/2" });

        await sut.CreateCheckoutSessionAsync(_user, PricingTestData.Pack(), "https://s", "https://c", Metadata);

        var line = Assert.Single(Assert.Single(_sessions).LineItems);
        Assert.Null(line.Price);
        Assert.Equal(1425, line.PriceData.UnitAmount);
        Assert.Equal("EUR", line.PriceData.Currency);
        Assert.Equal("Signing usage", line.PriceData.ProductData.Name);
    }

    [Fact]
    public async Task SessionRefusedWithPrice_IsRetriedInline()
    {
        var sut = Create(Price300());
        SessionsAnswer(
            () => throw new StripeException("Stripe Tax needs a tax behavior"),
            () => new Session { Url = "https://checkout.test/3" });

        var url = await sut.CreateCheckoutSessionAsync(_user, PricingTestData.Pack(), "https://s", "https://c", Metadata);

        Assert.Equal("https://checkout.test/3", url);
        Assert.Equal(2, _sessions.Count);
        Assert.Equal("price_300", _sessions[0].LineItems[0].Price);
        Assert.Equal(1425, _sessions[1].LineItems[0].PriceData.UnitAmount);
    }

    [Fact]
    public async Task ConfiguredPack_IsChargedInlineWithoutAskingStripeForAPrice()
    {
        var sut = Create();
        SessionsAnswer(() => new Session { Url = "https://checkout.test/4" });
        var pack = new ConfigCreditPricingProvider(new BillingOptions()).GetPack(300)!;

        await sut.CreateCheckoutSessionAsync(_user, pack, "https://s", "https://c", Metadata);

        var line = Assert.Single(Assert.Single(_sessions).LineItems);
        Assert.Equal(1425, line.PriceData.UnitAmount);
        Assert.Equal("EUR", line.PriceData.Currency);
        _stripe.Verify(c => c.RequestAsync<StripeList<Price>>(
            It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AmountOverload_IsUnchanged()
    {
        var sut = Create();
        SessionsAnswer(() => new Session { Url = "https://checkout.test/5" });

        await sut.CreateCheckoutSessionAsync(_user, 500, "EUR", "https://s", "https://c", Metadata);

        var line = Assert.Single(Assert.Single(_sessions).LineItems);
        Assert.Equal(500, line.PriceData.UnitAmount);
        Assert.Null(_sessions[0].PaymentIntentData);
    }
}
