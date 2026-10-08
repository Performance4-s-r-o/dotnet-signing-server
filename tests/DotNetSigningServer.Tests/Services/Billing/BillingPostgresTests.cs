using System.Net;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>Grants and the auto-recharge claim against PostgreSQL (unique keys, transactions, two replicas).</summary>
[Trait("Category", "Db")]
public class BillingPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private static readonly CreditPack Pack300 = new(300, 1500, "EUR", "pd_credits_300_once_eur", "price_1");

    private readonly OutboxPostgresFixture _pg;
    private readonly StubServiceHandler _service = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);

    public BillingPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://backoffice.example.com/") };
    }

    private async Task<User> NewUserAsync()
    {
        var user = TestHelpers.CreateTestUser($"{Guid.NewGuid():N}@example.com");
        user.CreditsRemaining = 0;
        await using var db = _pg.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<int> CreditsAsync(Guid userId)
    {
        await using var db = _pg.CreateContext();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).CreditsRemaining;
    }

    private async Task<WebhookEvent?> ClaimRowAsync(Guid userId)
    {
        await using var db = _pg.CreateContext();
        return await db.WebhookEvents.AsNoTracking().SingleOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId));
    }

    private async Task AddRowAsync(WebhookEvent row)
    {
        await using var db = _pg.CreateContext();
        db.WebhookEvents.Add(row);
        await db.SaveChangesAsync();
    }

    private static WebhookEvent Claim(Guid userId, string key, DateTimeOffset first, DateTimeOffset claimedAt, bool retryReady) => new()
    {
        EventId = AutoRechargeClaim.EventIdFor(userId),
        EventType = AutoRechargeClaim.EventType,
        PayloadJson = JsonSerializer.Serialize(new { key, first }),
        ReceivedAt = claimedAt,
        ProcessedAt = retryReady ? claimedAt : null,
    };

    private BillingEventsHandler Handler(ApplicationDbContext db, IAutoRechargeService? autoRecharge = null)
    {
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.PricePer100).Returns(5m);
        var services = new ServiceCollection()
            .AddSingleton(autoRecharge ?? Mock.Of<IAutoRechargeService>())
            .AddSingleton(pricing.Object)
            .BuildServiceProvider();
        return new BillingEventsHandler(BackofficeMode.On, db, services, NullLogger<BillingEventsHandler>.Instance);
    }

    private BackofficeAutoRecharge Recharge(ApplicationDbContext db) =>
        new(db, new BackofficeBillingClient(new Factory(_service), NullLogger<BackofficeBillingClient>.Instance, _ => TimeSpan.Zero),
            _time, NullLogger<BackofficeAutoRecharge>.Instance);

    private static BackofficeEvent Event(string type, string data) =>
        new($"msg_{Guid.NewGuid():N}", type, JsonDocument.Parse(data).RootElement.Clone(), "webhook", DateTimeOffset.UtcNow, 1);

    private static BackofficeEvent CheckoutCompleted(Guid userId, string sessionId, bool autoRecharge = false) =>
        Event(BackofficeEventTypes.BillingCheckoutCompleted, $$$"""
            {"customer_ref":"{{{BillingCustomerRef.For(userId)}}}","kind":"payment","payment_status":"paid","stripe_session_id":"{{{sessionId}}}",
             "amount_total":1500,"currency":"eur","stripe_payment_intent_id":"pi_{{{sessionId}}}","stripe_customer_id":"cus_1",
             "metadata":{"userId":"{{{userId}}}","documents":"300","autoRecharge":"{{{autoRecharge}}}"}}
            """);

    private static BackofficeEvent AutoRechargePaid(Guid userId, string paymentIntent, string attemptKey) =>
        Event(BackofficeEventTypes.BillingPaymentSucceeded, $$$"""
            {"customer_ref":"{{{BillingCustomerRef.For(userId)}}}","stripe_payment_intent_id":"{{{paymentIntent}}}","status":"succeeded",
             "amount":1500,"currency":"eur","metadata":{"type":"auto_recharge","userId":"{{{userId}}}","documents":"300","rechargeAttempt":"{{{attemptKey}}}"}}
            """);

    private void EnqueueCharge(string status, TimeSpan? delay = null) =>
        _service.Responses.Enqueue(async _ =>
        {
            if (delay is { } d) await Task.Delay(d);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"pi_{{Guid.NewGuid():N}}","status":"{{status}}","amount":1500,"currency":"eur"}""",
                    Encoding.UTF8, "application/json"),
            };
        });

    [DockerFact]
    public async Task CheckoutCompleted_GrantsOnce_WithItsPayment()
    {
        var user = await NewUserAsync();

        await using (var db = _pg.CreateContext()) await Handler(db).HandleAsync(CheckoutCompleted(user.Id, "cs_once"), default);
        await using (var db = _pg.CreateContext()) await Handler(db).HandleAsync(CheckoutCompleted(user.Id, "cs_once"), default);

        Assert.Equal(300, await CreditsAsync(user.Id));
        await using var check = _pg.CreateContext();
        Assert.Single(await check.Payments.Where(p => p.UserId == user.Id).ToListAsync());
        Assert.Equal(CreditGrants.CheckoutConfirmType, (await check.WebhookEvents.SingleAsync(w => w.EventId == "cs_once")).EventType);
    }

    [DockerFact]
    public async Task CheckoutCompleted_AfterTheConfirmPage_GrantsNothing()
    {
        var user = await NewUserAsync();
        await AddRowAsync(new WebhookEvent { EventId = "cs_confirmed", EventType = CreditGrants.CheckoutConfirmType, PayloadJson = "{}" });

        await using (var db = _pg.CreateContext()) await Handler(db).HandleAsync(CheckoutCompleted(user.Id, "cs_confirmed"), default);

        Assert.Equal(0, await CreditsAsync(user.Id));
    }

    [DockerFact]
    public async Task CheckoutCompleted_FailingAlongside_RollsTheGrantBack()
    {
        var user = await NewUserAsync();
        var autoRecharge = new Mock<IAutoRechargeService>();
        autoRecharge.Setup(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>())).ThrowsAsync(new InvalidOperationException("boom"));

        await using (var db = _pg.CreateContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(db, autoRecharge.Object).HandleAsync(CheckoutCompleted(user.Id, "cs_rollback", autoRecharge: true), default));
        }

        // Nothing was kept, so the inbox's retry grants it in full.
        Assert.Equal(0, await CreditsAsync(user.Id));
        await using var check = _pg.CreateContext();
        Assert.False(await check.WebhookEvents.AnyAsync(w => w.EventId == "cs_rollback"));
        Assert.False(await check.Payments.AnyAsync(p => p.UserId == user.Id));
    }

    [DockerFact]
    public async Task PaymentSucceeded_AfterTheStripeWebhook_GrantsNothing_AndReleasesItsClaim()
    {
        var user = await NewUserAsync();
        await AddRowAsync(new WebhookEvent { EventId = CreditGrants.AutoRechargeKey("pi_hooked"), EventType = "auto_recharge", PayloadJson = "{}" });
        await AddRowAsync(Claim(user.Id, "k1", _time.Now, _time.Now, retryReady: false));

        await using (var db = _pg.CreateContext()) await Handler(db).HandleAsync(AutoRechargePaid(user.Id, "pi_hooked", "k1"), default);

        Assert.Equal(0, await CreditsAsync(user.Id));
        Assert.Null(await ClaimRowAsync(user.Id));
    }

    [DockerFact]
    public async Task LateEventOfAnEarlierCharge_LeavesTheRunningClaim()
    {
        var user = await NewUserAsync();
        await AddRowAsync(Claim(user.Id, "k2", _time.Now, _time.Now, retryReady: true));

        await using (var db = _pg.CreateContext()) await Handler(db).HandleAsync(AutoRechargePaid(user.Id, "pi_old", "k1"), default);

        Assert.Equal(300, await CreditsAsync(user.Id));
        Assert.Contains("k2", (await ClaimRowAsync(user.Id))!.PayloadJson);
    }

    [DockerFact]
    public async Task TwoReplicas_StartOneCharge()
    {
        var user = await NewUserAsync();
        EnqueueCharge("processing", TimeSpan.FromMilliseconds(300));
        EnqueueCharge("processing");

        await using var db1 = _pg.CreateContext();
        await using var db2 = _pg.CreateContext();
        var results = await Task.WhenAll(Recharge(db1).ChargeAsync(user, Pack300), Recharge(db2).ChargeAsync(user, Pack300));

        Assert.Single(_service.Requests);
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r?.Outcome == ServiceChargeOutcome.Pending);
        Assert.NotNull(await ClaimRowAsync(user.Id));
    }

    [DockerFact]
    public async Task AfterAnOutage_TheNextRunRepeatsTheSameKey_AndGrants()
    {
        var user = await NewUserAsync();
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        EnqueueCharge("succeeded");

        ServiceCharge? first;
        await using (var db = _pg.CreateContext()) first = await Recharge(db).ChargeAsync(user, Pack300);
        // A restart: a new process, a new context.
        ServiceCharge? second;
        await using (var db = _pg.CreateContext()) second = await Recharge(db).ChargeAsync(user, Pack300);

        Assert.Equal(ServiceChargeOutcome.RetryLater, first!.Outcome);
        Assert.Equal(ServiceChargeOutcome.Succeeded, second!.Outcome);
        Assert.True(second.CreditsGranted);
        var keys = _service.Requests.Select(r => r.Request.Headers.GetValues(BackofficeBillingClient.IdempotencyKeyHeader).Single()).Distinct().ToList();
        var key = Assert.Single(keys);
        Assert.Equal(key, JsonDocument.Parse(_service.Requests[^1].Body).RootElement.GetProperty("metadata").GetProperty("rechargeAttempt").GetString());
        Assert.Equal(300, await CreditsAsync(user.Id));
        Assert.Null(await ClaimRowAsync(user.Id));
    }

    [DockerFact]
    public async Task ClaimPastTheIdempotencyWindow_IsNotRepeated()
    {
        var user = await NewUserAsync();
        await AddRowAsync(Claim(user.Id, "k_old", _time.Now - TimeSpan.FromHours(24), _time.Now.AddMinutes(-5), retryReady: true));

        ServiceCharge? charge;
        await using (var db = _pg.CreateContext()) charge = await Recharge(db).ChargeAsync(user, Pack300);

        Assert.Equal(ServiceChargeOutcome.Abandoned, charge!.Outcome);
        Assert.Empty(_service.Requests);
        Assert.Null(await ClaimRowAsync(user.Id));
    }
}
