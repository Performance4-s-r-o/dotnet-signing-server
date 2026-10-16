using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>The product's Stripe webhook (#79): a failed event is retried by Stripe and grants once.</summary>
public class StripeWebhookRetryTests
{
    private const string Secret = "whsec_test_secret_for_webhook_retry";

    private readonly string _dbName = "stripe-webhook-" + Guid.NewGuid();
    private readonly Mock<IAutoRechargeService> _autoRecharge = new();

    private ApplicationDbContext Db() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_dbName).Options);

    private StripeWebhookController Controller(ApplicationDbContext db, string body, string billingMode = "Off")
    {
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.PricePer100).Returns(5m);
        var backoffice = new P4BackofficeProductOptions
        {
            Modules = { Billing = billingMode },
            DisabledReason = BackofficeDisabledReason.None,
        };
        var controller = new StripeWebhookController(
            db,
            MsOptions.Create(new StripeOptions { WebhookSecret = Secret }),
            MsOptions.Create(new BillingOptions()),
            _autoRecharge.Object,
            Mock.Of<ITemplatedEmailSender>(),
            MsOptions.Create(new AppOptions()),
            NullLogger<StripeWebhookController>.Instance,
            pricing.Object,
            MsOptions.Create(backoffice));

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["Stripe-Signature"] = Sign(body);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static string Sign(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        return $"t={timestamp},v1={signature}";
    }

    private static string CheckoutCompleted(string eventId, Guid userId, string sessionId) =>
        $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2024-06-20",
          "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
          "livemode": false,
          "type": "checkout.session.completed",
          "data": {
            "object": {
              "id": "{{sessionId}}",
              "object": "checkout.session",
              "mode": "payment",
              "status": "complete",
              "payment_status": "paid",
              "customer": "cus_buyer",
              "payment_intent": "pi_{{sessionId}}",
              "amount_total": 1500,
              "currency": "eur",
              "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
              "metadata": {
                "userId": "{{userId}}",
                "documents": "300",
                "autoRecharge": "true",
                "{{BillingCustomerRef.ProductMarkerKey}}": "{{BillingCustomerRef.ProductMarker}}"
              }
            }
          }
        }
        """;

    private async Task<Guid> NewUserAsync()
    {
        var user = TestHelpers.CreateTestUser("buyer@example.com");
        user.CreditsRemaining = 0;
        await using var db = Db();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<int> CreditsAsync(Guid userId)
    {
        await using var db = Db();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.CreditsRemaining).SingleAsync();
    }

    private async Task<int> DeliverAsync(string body, string billingMode = "Off")
    {
        await using var db = Db();
        var result = await Controller(db, body, billingMode).HandleWebhook();
        return result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => throw new InvalidOperationException(result.GetType().Name),
        };
    }

    [Fact]
    public async Task FailedCheckout_Answers500_StripeRetries_AndTheCreditsAreGrantedOnce()
    {
        var userId = await NewUserAsync();
        var body = CheckoutCompleted("evt_retry", userId, "cs_retry");
        _autoRecharge.SetupSequence(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()))
            .ThrowsAsync(new TimeoutException("database timeout"))
            .Returns(Task.CompletedTask);

        // First delivery fails: Stripe must be told to retry, and nothing may be granted.
        Assert.Equal(StatusCodes.Status500InternalServerError, await DeliverAsync(body));
        Assert.Equal(0, await CreditsAsync(userId));
        await using (var db = Db())
        {
            var failed = await db.WebhookEvents.AsNoTracking().SingleAsync(w => w.EventId == "evt_retry");
            Assert.Null(failed.ProcessedAt);
            Assert.Contains("database timeout", failed.Error);
            Assert.False(await CreditGrants.IsGrantedAsync(db, "cs_retry", default));
        }

        // Stripe's retry is handled, not skipped as a duplicate.
        Assert.Equal(StatusCodes.Status200OK, await DeliverAsync(body));
        Assert.Equal(300, await CreditsAsync(userId));

        // Another redelivery of the handled event is a duplicate.
        Assert.Equal(StatusCodes.Status200OK, await DeliverAsync(body));
        Assert.Equal(300, await CreditsAsync(userId));

        await using var check = Db();
        var row = await check.WebhookEvents.AsNoTracking().SingleAsync(w => w.EventId == "evt_retry");
        Assert.NotNull(row.ProcessedAt);
        Assert.Null(row.Error);
        var grant = await check.CreditGrants.AsNoTracking().SingleAsync();
        Assert.Equal("cs_retry", grant.GrantKey);
        Assert.Equal("pi_cs_retry", grant.PaymentIntentId);
        Assert.Equal("stripe-webhook", grant.Source);
        Assert.Single(await check.Payments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AnotherEventForTheSamePayment_GrantsNothingMore()
    {
        var userId = await NewUserAsync();
        _autoRecharge.Setup(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>())).Returns(Task.CompletedTask);

        Assert.Equal(200, await DeliverAsync(CheckoutCompleted("evt_a", userId, "cs_same")));
        Assert.Equal(200, await DeliverAsync(CheckoutCompleted("evt_b", userId, "cs_same")));

        Assert.Equal(300, await CreditsAsync(userId));
    }

    [Fact]
    public async Task BillingOn_TheEndpointIsOff()
    {
        var userId = await NewUserAsync();

        Assert.Equal(StatusCodes.Status200OK, await DeliverAsync(CheckoutCompleted("evt_on", userId, "cs_on"), billingMode: "On"));

        Assert.Equal(0, await CreditsAsync(userId));
        await using var db = Db();
        Assert.Empty(await db.WebhookEvents.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public async Task BillingOffOrShadow_TheEndpointGrants(string mode)
    {
        var userId = await NewUserAsync();
        _autoRecharge.Setup(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>())).Returns(Task.CompletedTask);

        Assert.Equal(200, await DeliverAsync(CheckoutCompleted("evt_" + mode, userId, "cs_" + mode), mode));

        Assert.Equal(300, await CreditsAsync(userId));
    }
}
