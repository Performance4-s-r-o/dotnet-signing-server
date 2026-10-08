using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Billing;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>Pure rules of payments through the billing API.</summary>
public class BillingDomainTests
{
    private static readonly Guid UserId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly string Ref = "user:6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void CustomerRef_RoundTripsAndMatchesTheServicePattern()
    {
        Assert.Equal(Ref, BillingCustomerRef.For(UserId));
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]{2,199}$", BillingCustomerRef.For(UserId));
        Assert.True(BillingCustomerRef.TryParse(Ref, out var parsed));
        Assert.Equal(UserId, parsed);
        Assert.False(BillingCustomerRef.TryParse("tenant_42", out _));
        Assert.False(BillingCustomerRef.TryParse(null, out _));
    }

    [Theory]
    [InlineData(null, null, true)] // direct path: no metadata check possible beyond what is there
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", null, true)]
    [InlineData("00000000-0000-0000-0000-000000000001", null, false)]
    [InlineData(null, "user:6f9619ff-8b86-d011-b42d-00c04fc964ff", true)]
    [InlineData(null, "user:00000000-0000-0000-0000-000000000001", false)]
    [InlineData(null, "", false)] // a service session without a customer belongs to nobody
    public void Checkout_BelongsOnlyToItsUser(string? metaUserId, string? customerRef, bool expected)
    {
        var metadata = metaUserId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["userId"] = metaUserId };
        var session = new CheckoutSessionInfo("cs_1", "payment", "complete", "paid", metadata, null, customerRef);

        Assert.Equal(expected, BillingCustomerRef.CheckoutBelongsTo(session, UserId));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(500, null, true)]
    [InlineData(502, "stripe_error", true)]
    [InlineData(429, "rate_limited", true)]
    [InlineData(409, "idempotency_in_progress", true)]
    [InlineData(409, "no_payment_method", false)]
    [InlineData(409, "billing_not_configured", false)]
    [InlineData(422, "stripe_error", false)]
    [InlineData(422, "idempotency_key_reused", false)]
    [InlineData(404, "customer_not_found", false)]
    [InlineData(200, null, false)]
    public void Retry_SameKeyOnlyWhenTheRequestMayHaveRunOrWillRun(int? status, string? code, bool expected)
    {
        Assert.Equal(expected, BillingRetryPolicy.RetryWithSameKey(new BillingApiResult(status, null, code, null)));
    }

    [Fact]
    public void ProblemBody_GivesCodeAndStripeCode()
    {
        var result = BillingApiResult.From(422, """{"type":"about:blank","status":422,"code":"stripe_error","stripe_code":"card_declined"}""");

        Assert.Equal("stripe_error", result.ProblemCode);
        Assert.Equal("card_declined", result.StripeCode);
        Assert.Equal("HTTP 422 stripe_error (card_declined)", result.Describe());
        Assert.Null(BillingApiResult.From(502, "<html>bad gateway</html>").ProblemCode);
    }

    [Theory]
    [InlineData(200, """{"id":"pi_1","status":"succeeded","amount":1500,"currency":"eur"}""", ServiceChargeOutcome.Succeeded, "pi_1")]
    [InlineData(200, """{"id":"pi_1","status":"processing","amount":1500,"currency":"eur"}""", ServiceChargeOutcome.Pending, "pi_1")]
    [InlineData(200, """{"id":"pi_1","status":"requires_action","client_secret":"x"}""", ServiceChargeOutcome.RequiresAction, "pi_1")]
    [InlineData(200, """{"id":"pi_1","status":"failed","decline_code":"insufficient_funds"}""", ServiceChargeOutcome.Declined, "pi_1")]
    [InlineData(200, """{"id":"pi_1","status":"something_new"}""", ServiceChargeOutcome.Pending, "pi_1")]
    [InlineData(502, """{"code":"stripe_error"}""", ServiceChargeOutcome.RetryLater, null)]
    [InlineData(409, """{"code":"no_payment_method"}""", ServiceChargeOutcome.Refused, null)]
    [InlineData(409, """{"code":"daily_charge_limit_reached"}""", ServiceChargeOutcome.Refused, null)]
    public void Charge_AnswerMapsToOutcome(int status, string body, ServiceChargeOutcome expected, string? paymentIntent)
    {
        var charge = ServiceCharge.From(BillingApiResult.From(status, body));

        Assert.Equal(expected, charge.Outcome);
        Assert.Equal(paymentIntent, charge.PaymentIntentId);
    }

    [Fact]
    public void Charge_DeclineCarriesTheDeclineCode()
    {
        var charge = ServiceCharge.From(BillingApiResult.From(200, """{"id":"pi_1","status":"failed","decline_code":"insufficient_funds"}"""));
        Assert.Equal("insufficient_funds", charge.Reason);
        Assert.Contains("insufficient_funds", AutoRechargeServiceFailureReason(charge));
    }

    private static string AutoRechargeServiceFailureReason(ServiceCharge charge) =>
        DotNetSigningServer.Services.AutoRechargeService.FailureReason(charge);

    [Fact]
    public void Claim_NoneStartsNew_RunningIsBusy_StaleOrRetryReadyIsReused()
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

        Assert.Equal(ClaimDecision.New, AutoRechargeClaim.Decide(null, now));
        Assert.Equal(ClaimDecision.Busy, AutoRechargeClaim.Decide(new("k", now.AddMinutes(-1), false), now));
        Assert.Equal(ClaimDecision.Reuse, AutoRechargeClaim.Decide(new("k", now.AddMinutes(-1), true), now));
        Assert.Equal(ClaimDecision.Reuse, AutoRechargeClaim.Decide(new("k", now - AutoRechargeClaim.StaleAfter, false), now));
    }

    [Theory]
    [InlineData(ServiceChargeOutcome.Succeeded, false, false)]
    [InlineData(ServiceChargeOutcome.Declined, false, false)]
    [InlineData(ServiceChargeOutcome.RequiresAction, false, false)]
    [InlineData(ServiceChargeOutcome.Refused, false, false)]
    [InlineData(ServiceChargeOutcome.RetryLater, true, true)]
    [InlineData(ServiceChargeOutcome.Pending, true, false)]
    public void Claim_StaysOnlyWhileTheChargeIsUnfinished(ServiceChargeOutcome outcome, bool keep, bool retryReady)
    {
        Assert.Equal((keep, retryReady), AutoRechargeClaim.After(outcome));
    }

    [Fact]
    public void Claim_RowWithoutKeyIsNotAClaim()
    {
        Assert.Null(BackofficeAutoRecharge.Read(new WebhookEvent { PayloadJson = "{}" }));
        Assert.Null(BackofficeAutoRecharge.Read(new WebhookEvent { PayloadJson = "not json" }));
        var claim = BackofficeAutoRecharge.Read(new WebhookEvent { PayloadJson = """{"key":"k1"}""", ProcessedAt = DateTimeOffset.UtcNow });
        Assert.Equal("k1", claim!.Key);
        Assert.True(claim.RetryReady);
    }

    private static string CheckoutEvent(string customerRef, string paymentStatus = "paid", string kind = "payment", string documents = "300") => $$$"""
        {"stripe_event_id":"evt_1","customer_ref":"{{{customerRef}}}","kind":"{{{kind}}}","payment_status":"{{{paymentStatus}}}",
         "stripe_session_id":"cs_test_1","amount_total":1500,"currency":"eur","stripe_payment_intent_id":"pi_1",
         "stripe_customer_id":"cus_1","metadata":{"userId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","documents":"{{{documents}}}","autoRecharge":"True"}}
        """;

    [Fact]
    public void CheckoutCompleted_PaidPurchaseOfTheUser()
    {
        var purchase = BillingEventData.Purchase(Json(CheckoutEvent(Ref)), out var reason);

        Assert.Null(reason);
        Assert.Equal(new CheckoutPurchase("cs_test_1", UserId, 300, true, 1500, "eur", "pi_1", "cus_1"), purchase);
    }

    [Theory]
    [InlineData("user:00000000-0000-0000-0000-000000000001", "paid", "payment", "300", "customer_ref does not match userId")]
    [InlineData("user:6f9619ff-8b86-d011-b42d-00c04fc964ff", "unpaid", "payment", "300", "not paid")]
    [InlineData("user:6f9619ff-8b86-d011-b42d-00c04fc964ff", "paid", "setup", "300", "mode setup")]
    [InlineData("user:6f9619ff-8b86-d011-b42d-00c04fc964ff", "paid", "payment", "0", "no documents in metadata")]
    public void CheckoutCompleted_GrantsNothingItCannotTrust(string customerRef, string paymentStatus, string kind, string documents, string expected)
    {
        Assert.Null(BillingEventData.Purchase(Json(CheckoutEvent(customerRef, paymentStatus, kind, documents)), out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void PaymentEvent_OnlyAutoRechargeOfTheUser()
    {
        var data = Json($$$"""
            {"customer_ref":"{{{Ref}}}","stripe_payment_intent_id":"pi_9","status":"succeeded","amount":4250,"currency":"eur",
             "metadata":{"type":"auto_recharge","userId":"{{{UserId}}}","documents":"1000"}}
            """);
        var payment = BillingEventData.AutoRecharge(data, out _);
        Assert.Equal(new AutoRechargePayment("pi_9", UserId, 1000, 4250, "eur", null), payment);

        var checkoutPayment = Json($$$"""{"customer_ref":"{{{Ref}}}","stripe_payment_intent_id":"pi_9","metadata":{"userId":"{{{UserId}}}","documents":"100"}}""");
        Assert.Null(BillingEventData.AutoRecharge(checkoutPayment, out var reason));
        Assert.Equal("not an auto-recharge", reason);
    }

    [Fact]
    public void Detached_OnlyForCustomersOfThisProduct()
    {
        Assert.Equal(UserId, BillingEventData.DetachedFrom(Json($$$"""{"customer_ref":"{{{Ref}}}"}""")));
        Assert.Null(BillingEventData.DetachedFrom(Json("""{"customer_ref":null}""")));
    }

    [Fact]
    public void BillingModule_NeverInheritsTheGlobalMode()
    {
        var options = new P4BackofficeProductOptions { Mode = "On" };
        Assert.Equal(BackofficeMode.On, options.RequestedModeFor(BackofficeModule.Pricing));
        Assert.Equal(BackofficeMode.Off, options.RequestedModeFor(BackofficeModule.Billing));

        options.Modules.Billing = "Shadow";
        Assert.Equal(BackofficeMode.Shadow, options.ModeFor(BackofficeModule.Billing));
    }

    [Fact]
    public void BillingOn_NeedsPricingOn()
    {
        var options = new P4BackofficeProductOptions
        {
            BaseUrl = "https://backoffice.example.com",
            SecretKey = "p4sk_test_abc",
            Modules = { Billing = "On", Pricing = "Shadow" },
        };
        Assert.Contains(BackofficeOptionsValidator.Validate(options), p => p.Contains("Modules__Billing"));

        options.Modules.Pricing = "On";
        Assert.Empty(BackofficeOptionsValidator.Validate(options));

        options.Modules.Pricing = "Off";
        options.DisabledReason = BackofficeDisabledReason.PrivateServer;
        Assert.Empty(BackofficeOptionsValidator.Validate(options));
    }
}

/// <summary>Which payment path is wired, by <c>Modules:Billing</c>.</summary>
public class BillingRegistrationTests
{
    private static Microsoft.Extensions.DependencyInjection.ServiceCollection Register(string? billing, bool privateServer = false) =>
        DotNetSigningServer.Tests.Services.Backoffice.BackofficeRegistrationTests.Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:Modules:Billing"] = billing,
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        }, privateServer);

    private static bool Has<T>(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(T));

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public void NotOn_PaysThroughStripeDirectly(string? billing)
    {
        var services = Register(billing);

        Assert.True(Has<StripePaymentGateway>(services));
        Assert.True(Has<IPaymentGateway>(services));
        Assert.False(Has<BackofficePaymentGateway>(services));
        Assert.False(Has<BackofficeAutoRecharge>(services));
    }

    [Fact]
    public void On_PaysThroughTheService()
    {
        var services = Register("On");

        Assert.True(Has<BackofficePaymentGateway>(services));
        Assert.True(Has<BackofficeAutoRecharge>(services));
        Assert.True(Has<BackofficeBillingClient>(services));
    }

    [Fact]
    public void PrivateServer_IsForcedOff()
    {
        Assert.False(Has<BackofficePaymentGateway>(Register("On", privateServer: true)));
    }
}
