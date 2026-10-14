using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>
/// #82: a Stripe object naming a user in its metadata acts on that user only when its customer
/// is theirs, and a user is never linked to a customer another user holds.
/// </summary>
[Trait("Category", "Db")]
public class StripeCustomerOwnershipTests : IClassFixture<OutboxPostgresFixture>
{
    private const string WebhookSecret = "whsec_test_ownership";

    private readonly OutboxPostgresFixture _pg;
    private readonly Mock<IAutoRechargeService> _autoRecharge = new();

    public StripeCustomerOwnershipTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private async Task<User> NewUserAsync(string? stripeCustomerId)
    {
        var user = TestHelpers.CreateTestUser($"{Guid.NewGuid():N}@example.com");
        user.CreditsRemaining = 0;
        user.StripeCustomerId = stripeCustomerId;
        await using var db = _pg.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<User> ReloadAsync(Guid userId)
    {
        await using var db = _pg.CreateContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    // --- Stripe webhook ---------------------------------------------------------------

    private async Task PostWebhookAsync(string dataObjectJson, string type)
    {
        var payload = $$$"""
            {"id":"{{{Unique("evt")}}}","object":"event","api_version":"2024-06-20","created":{{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}},
             "livemode":false,"pending_webhooks":1,"type":"{{{type}}}","data":{"object":{{{dataObjectJson}}}}}
            """;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

        await using var db = _pg.CreateContext();
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.PricePer100).Returns(5m);
        var controller = new StripeWebhookController(
            db,
            TestHelpers.WrapOptions(new StripeOptions { WebhookSecret = WebhookSecret }),
            TestHelpers.WrapOptions(new BillingOptions()),
            _autoRecharge.Object,
            Mock.Of<ITemplatedEmailSender>(),
            TestHelpers.WrapOptions(new AppOptions()),
            NullLogger<StripeWebhookController>.Instance,
            pricing.Object);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        http.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.HandleWebhook();
        Assert.IsType<OkResult>(result);
    }

    private static string CheckoutSession(Guid userId, string? customerId, bool autoRecharge = false) => $$$"""
        {"id":"{{{Unique("cs")}}}","object":"checkout.session","mode":"payment","status":"complete","payment_status":"paid",
         "customer":{{{(customerId is null ? "null" : $"\"{customerId}\"")}}},"amount_total":1500,"currency":"eur",
         "payment_intent":"{{{Unique("pi")}}}",
         "metadata":{"userId":"{{{userId}}}","documents":"300","autoRecharge":"{{{autoRecharge}}}"}}
        """;

    private static string AutoRechargeIntent(Guid userId, string customerId) => $$$"""
        {"id":"{{{Unique("pi")}}}","object":"payment_intent","status":"succeeded","amount":1500,"currency":"eur",
         "customer":"{{{customerId}}}","metadata":{"type":"auto_recharge","userId":"{{{userId}}}","documents":"300"}}
        """;

    [Fact]
    public async Task Webhook_CheckoutOfAnotherCustomer_GrantsNothing_AndDoesNotRelink()
    {
        var victim = await NewUserAsync(Unique("cus_victim"));
        var attackerCustomer = Unique("cus_other");

        await PostWebhookAsync(CheckoutSession(victim.Id, attackerCustomer, autoRecharge: true), "checkout.session.completed");

        var after = await ReloadAsync(victim.Id);
        Assert.Equal(0, after.CreditsRemaining);
        Assert.Equal(victim.StripeCustomerId, after.StripeCustomerId);
        _autoRecharge.Verify(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_CheckoutOfTheUsersCustomer_Grants()
    {
        var user = await NewUserAsync(Unique("cus_own"));

        await PostWebhookAsync(CheckoutSession(user.Id, user.StripeCustomerId), "checkout.session.completed");

        Assert.Equal(300, (await ReloadAsync(user.Id)).CreditsRemaining);
    }

    [Fact]
    public async Task Webhook_CheckoutWithACustomerHeldByAnotherUser_IsNotLinkedNorGranted()
    {
        var holder = await NewUserAsync(Unique("cus_held"));
        var user = await NewUserAsync(stripeCustomerId: null);

        await PostWebhookAsync(CheckoutSession(user.Id, holder.StripeCustomerId), "checkout.session.completed");

        var after = await ReloadAsync(user.Id);
        Assert.Equal(0, after.CreditsRemaining);
        Assert.Null(after.StripeCustomerId);
    }

    [Fact]
    public async Task Webhook_CheckoutOfAFreshCustomer_LinksItToAUserWithoutOne()
    {
        var user = await NewUserAsync(stripeCustomerId: null);
        var fresh = Unique("cus_fresh");

        await PostWebhookAsync(CheckoutSession(user.Id, fresh), "checkout.session.completed");

        var after = await ReloadAsync(user.Id);
        Assert.Equal(300, after.CreditsRemaining);
        Assert.Equal(fresh, after.StripeCustomerId);
    }

    [Fact]
    public async Task Webhook_AutoRechargeOfAnotherCustomer_GrantsNothing()
    {
        var user = await NewUserAsync(Unique("cus_own"));

        await PostWebhookAsync(AutoRechargeIntent(user.Id, Unique("cus_other")), "payment_intent.succeeded");
        Assert.Equal(0, (await ReloadAsync(user.Id)).CreditsRemaining);

        await PostWebhookAsync(AutoRechargeIntent(user.Id, user.StripeCustomerId!), "payment_intent.succeeded");
        Assert.Equal(300, (await ReloadAsync(user.Id)).CreditsRemaining);
    }

    // --- Confirm pages (redirect from Stripe) ---------------------------------------------

    private BillingController Billing(ApplicationDbContext db, User user, Stripe.Checkout.Session session)
    {
        var checkout = new Mock<IStripeCheckoutService>();
        checkout.Setup(c => c.GetSessionAsync(session.Id)).ReturnsAsync(session);
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.PricePer100).Returns(5m);
        var controller = new BillingController(
            db,
            new BillingService(TestHelpers.WrapOptions(new BillingOptions())),
            pricing.Object,
            new StripePaymentGateway(checkout.Object, NullLogger<StripePaymentGateway>.Instance),
            _autoRecharge.Object,
            NullLogger<BillingController>.Instance,
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        controller.Url = Mock.Of<IUrlHelper>();
        return controller;
    }

    private static Stripe.Checkout.Session SetupSession(Guid userId, string? customerId) => new()
    {
        Id = Unique("cs"),
        Mode = "setup",
        Status = "complete",
        CustomerId = customerId,
        Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString(), ["autoRechargeQuantity"] = "300" },
    };

    private static Stripe.Checkout.Session PaidSession(Guid userId, string? customerId) => new()
    {
        Id = Unique("cs"),
        Mode = "payment",
        Status = "complete",
        PaymentStatus = "paid",
        CustomerId = customerId,
        Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString(), ["documents"] = "300" },
    };

    [Fact]
    public async Task ConfirmAutoRechargeSetup_WithAnotherUsersCustomer_DoesNotLinkIt()
    {
        var holder = await NewUserAsync(Unique("cus_held"));
        var user = await NewUserAsync(stripeCustomerId: null);
        var session = SetupSession(user.Id, holder.StripeCustomerId);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmAutoRechargeSetup(session.Id);
        }

        Assert.Null((await ReloadAsync(user.Id)).StripeCustomerId);
        _autoRecharge.Verify(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAutoRechargeSetup_OfAnotherCustomer_DoesNotEnableAutoRecharge()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var session = SetupSession(user.Id, Unique("cus_other"));

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmAutoRechargeSetup(session.Id);
        }

        Assert.Equal(user.StripeCustomerId, (await ReloadAsync(user.Id)).StripeCustomerId);
        _autoRecharge.Verify(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmCheckout_OfAnotherCustomer_GrantsNothing_AndOwnCustomerGrants()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var foreign = PaidSession(user.Id, Unique("cus_other"));
        var own = PaidSession(user.Id, user.StripeCustomerId);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, foreign).ConfirmCheckout(foreign.Id);
        }
        Assert.Equal(0, (await ReloadAsync(user.Id)).CreditsRemaining);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, own).ConfirmCheckout(own.Id);
        }
        Assert.Equal(300, (await ReloadAsync(user.Id)).CreditsRemaining);
    }

    [Fact]
    public async Task ConfirmCheckout_WithoutUserIdMetadata_GrantsNothing()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var session = PaidSession(user.Id, user.StripeCustomerId);
        session.Metadata.Remove("userId");

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmCheckout(session.Id);
        }

        Assert.Equal(0, (await ReloadAsync(user.Id)).CreditsRemaining);
    }
}
