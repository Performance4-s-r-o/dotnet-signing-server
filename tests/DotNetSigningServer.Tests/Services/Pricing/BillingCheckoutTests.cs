using System.Security.Claims;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary><c>POST /Billing/Checkout</c> reads the pack from the pricing provider only.</summary>
public class BillingCheckoutTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();
    private readonly Mock<IStripeCheckoutService> _checkout = new();
    private readonly User _user;

    public BillingCheckoutTests()
    {
        _user = TestHelpers.CreateTestUser();
        _user.StripeCustomerId = "cus_1";
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private BillingController Controller(ICreditPricingProvider pricing)
    {
        var controller = new BillingController(
            _db,
            new BillingService(TestHelpers.WrapOptions(new BillingOptions())),
            pricing,
            _checkout.Object,
            Mock.Of<IAutoRechargeService>(),
            NullLogger<BillingController>.Instance,
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString())], "test")),
        };
        http.Request.Scheme = "https";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("https://app.test/Billing");
        controller.Url = url.Object;
        return controller;
    }

    private static BackofficeCreditPricingProvider PriceListProvider(string json)
    {
        var holder = new PricingSnapshotHolder();
        holder.Set(PricingTestData.Snapshot(json));
        return new BackofficeCreditPricingProvider(holder, new ConfigCreditPricingProvider(new BillingOptions()),
            new Monitor<StripeOptions>(new StripeOptions()), NullLogger<BackofficeCreditPricingProvider>.Instance);
    }

    [Fact]
    public async Task Pack300_IsCheckedOutWithItsLookupKeyAndDocumentsMetadata()
    {
        CreditPack? charged = null;
        IDictionary<string, string>? metadata = null;
        _checkout.Setup(c => c.CreateCheckoutSessionAsync(It.IsAny<User>(), It.IsAny<CreditPack>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>?>(), It.IsAny<bool>()))
            .Callback<User, CreditPack, string, string, IDictionary<string, string>?, bool>((_, p, _, _, m, _) => (charged, metadata) = (p, m))
            .ReturnsAsync("https://checkout.test/1");
        var controller = Controller(PriceListProvider(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [300] = 1500 })));

        var result = await controller.Checkout(300);

        Assert.Equal("https://checkout.test/1", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("pd_credits_300_once_eur", charged!.LookupKey);
        Assert.Equal(1500, charged.UnitAmountMinor);
        Assert.Equal("300", metadata!["documents"]);
    }

    [Fact]
    public async Task ConfiguredPrices_ChargeTheSameAmountAsBefore()
    {
        CreditPack? charged = null;
        _checkout.Setup(c => c.CreateCheckoutSessionAsync(It.IsAny<User>(), It.IsAny<CreditPack>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>?>(), It.IsAny<bool>()))
            .Callback<User, CreditPack, string, string, IDictionary<string, string>?, bool>((_, p, _, _, _, _) => charged = p)
            .ReturnsAsync("https://checkout.test/2");
        var controller = Controller(new ConfigCreditPricingProvider(new BillingOptions()));

        await controller.Checkout(1000);

        Assert.Equal(4250, charged!.UnitAmountMinor);
        Assert.Equal("EUR", charged.Currency);
        Assert.Null(charged.LookupKey);
    }

    [Fact]
    public async Task UnsoldQuantity_IsRefused()
    {
        var controller = Controller(new ConfigCreditPricingProvider(new BillingOptions()));

        var result = await controller.Checkout(200);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("InvalidDocumentBundle", controller.TempData["Error"]);
        _checkout.VerifyNoOtherCalls();
    }
}
