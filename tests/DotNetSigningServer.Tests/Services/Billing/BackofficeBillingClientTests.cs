using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>The billing API client and gateway against a stubbed service.</summary>
public class BackofficeBillingClientTests
{
    private readonly StubServiceHandler _service = new();
    private readonly User _user = new() { Id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), Email = "payer@example.com", Locale = "cs" };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://backoffice.example.com/") };
    }

    private BackofficeBillingClient Client() =>
        new(new Factory(_service), NullLogger<BackofficeBillingClient>.Instance, _ => TimeSpan.Zero);

    private BackofficePaymentGateway Gateway()
    {
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.Currency).Returns("EUR");
        return new BackofficePaymentGateway(Client(), pricing.Object, NullLogger<BackofficePaymentGateway>.Instance);
    }

    private static CreditPack Pack300 => new(300, 1500, "EUR", "pd_credits_300_once_eur", "price_1");

    private string Key(int request) =>
        _service.Requests[request].Request.Headers.GetValues(BackofficeBillingClient.IdempotencyKeyHeader).Single();

    private JsonElement Body(int request) => JsonDocument.Parse(_service.Requests[request].Body).RootElement;

    [Fact]
    public async Task Charge_AfterAnOutage_IsRepeatedWithTheSameKey()
    {
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        _service.Enqueue(HttpStatusCode.OK, """{"id":"pi_1","status":"succeeded","amount":1500,"currency":"eur"}""");

        var result = await Client().CreateChargeAsync(new { customer_ref = "user:x" }, "key-1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _service.Requests.Count);
        Assert.Equal("key-1", Key(0));
        Assert.Equal("key-1", Key(1));
        Assert.Equal("/v1/billing/charges", _service.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task InteractiveCall_IsSentOnce_EvenOnAnOutage()
    {
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");

        var result = await Client().CreateCheckoutAsync(new { customer_ref = "user:x" }, CancellationToken.None);

        Assert.Equal(502, result.Status);
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task Refusal_IsNotRepeated_AndTheNextWriteGetsANewKey()
    {
        _service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, "validation_failed");
        _service.Enqueue(HttpStatusCode.Created, """{"url":"https://billing.stripe.test/p"}""");

        var refused = await Client().CreatePortalSessionAsync(new { customer_ref = "user:x" }, CancellationToken.None);
        await Client().CreatePortalSessionAsync(new { customer_ref = "user:x" }, CancellationToken.None);

        Assert.Equal("validation_failed", refused.ProblemCode);
        Assert.Equal(2, _service.Requests.Count);
        Assert.NotEqual(Key(0), Key(1));
    }

    [Fact]
    public async Task Checkout_SendsTheCatalogPriceOnly_WithTheProductsMetadata()
    {
        _service.Enqueue(HttpStatusCode.Created, """{"id":"cs_test_1","ui":"hosted","url":"https://checkout.stripe.test/c","client_secret":null,"publishable_key":null,"expires_at":"2026-10-09T12:00:00Z"}""");

        var url = await Gateway().StartCheckoutAsync(_user, Pack300, "https://app.test/ok?session_id={CHECKOUT_SESSION_ID}", "https://app.test/billing",
            new Dictionary<string, string> { ["userId"] = _user.Id.ToString(), ["documents"] = "300", ["autoRecharge"] = "False" }, saveCard: true);

        Assert.Equal("https://checkout.stripe.test/c", url);
        var body = Body(0);
        Assert.Equal("user:6f9619ff-8b86-d011-b42d-00c04fc964ff", body.GetProperty("customer_ref").GetString());
        Assert.Equal("payer@example.com", body.GetProperty("customer").GetProperty("email").GetString());
        Assert.Equal("cs", body.GetProperty("customer").GetProperty("locale").GetString());
        Assert.Equal("payment", body.GetProperty("mode").GetString());
        Assert.Equal("hosted", body.GetProperty("ui").GetString());
        Assert.Equal("pd_credits_300_once_eur", body.GetProperty("items")[0].GetProperty("lookup_key").GetString());
        Assert.Equal(1, body.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.False(body.GetProperty("items")[0].TryGetProperty("price_data", out _));
        Assert.Equal("eur", body.GetProperty("currency").GetString());
        Assert.True(body.GetProperty("save_card").GetBoolean());
        Assert.True(body.GetProperty("invoice").GetBoolean());
        Assert.False(body.GetProperty("single_open_checkout").GetBoolean());
        Assert.Contains("{CHECKOUT_SESSION_ID}", body.GetProperty("success_url").GetString());
        Assert.All(body.GetProperty("metadata").EnumerateObject(), p => Assert.False(p.Name.StartsWith("p4_")));
        Assert.Equal("300", body.GetProperty("metadata").GetProperty("documents").GetString());
        Assert.False(string.IsNullOrWhiteSpace(Key(0)));
    }

    [Fact]
    public async Task Checkout_OfAPackWithoutLookupKey_IsNotSent()
    {
        var configured = new CreditPack(300, 1500, "EUR", null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway().StartCheckoutAsync(
            _user, configured, "https://app.test/ok", "https://app.test/billing", new Dictionary<string, string>(), false));
        Assert.Empty(_service.Requests);
    }

    [Fact]
    public async Task Checkout_ReturnUrlRefused_SurfacesTheProblemCode()
    {
        _service.Enqueue(HttpStatusCode.UnprocessableEntity,
            """{"code":"validation_failed","errors":[{"path":"success_url","message":"return_url_origin_not_allowed"}]}""");

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => Gateway().StartCheckoutAsync(
            _user, Pack300, "https://evil.test/ok", "https://app.test/billing", new Dictionary<string, string>(), false));

        Assert.False(ex.IsOutage);
        Assert.Contains("validation_failed", ex.Message);
        Assert.DoesNotContain("payer@example.com", ex.Message);
    }

    [Fact]
    public async Task GetCheckout_ReadsCustomerRefAndMetadata_AndUnknownSessionIsNull()
    {
        _service.Enqueue(HttpStatusCode.OK, """
            {"id":"cs_test_1","customer_ref":"user:6f9619ff-8b86-d011-b42d-00c04fc964ff","status":"complete","payment_status":"paid",
             "mode":"payment","metadata":{"userId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","documents":"300"},
             "subscription_id":null,"payment_id":"pi_1","setup_intent_id":null,"expires_at":"2026-10-09T12:00:00Z"}
            """);
        _service.EnqueueProblem(HttpStatusCode.NotFound, "checkout_not_found");

        var session = await Gateway().GetCheckoutAsync("cs_test_1");
        var missing = await Gateway().GetCheckoutAsync("cs_test_2");

        Assert.NotNull(session);
        Assert.Equal("paid", session.PaymentStatus);
        Assert.True(BillingCustomerRef.CheckoutBelongsTo(session, _user.Id));
        Assert.Equal("300", session.Metadata["documents"]);
        Assert.Null(missing);
        Assert.False(_service.Requests[0].Request.Headers.Contains(BackofficeBillingClient.IdempotencyKeyHeader));
    }

    [Fact]
    public async Task SavedCard_ForThePage_IsNullOnOutage_ButTheDetachCheckThrows()
    {
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");

        Assert.Null(await Gateway().GetSavedPaymentMethodAsync(_user));
        await Assert.ThrowsAsync<BillingApiException>(() => Gateway().HasSavedCardAsync(_user));
    }

    [Fact]
    public async Task SavedCard_IsReadFromTheCustomer()
    {
        _service.Enqueue(HttpStatusCode.OK, """
            {"customer_ref":"user:x","stripe_customer_id":"cus_1","created_at":"2026-10-01T00:00:00Z",
             "card":{"type":"card","brand":"visa","last4":"4242","exp_month":12,"exp_year":2030,"link_email":null},
             "subscriptions":[],"open_invoice":null,"notification_emails":[]}
            """);

        var card = await Gateway().GetSavedPaymentMethodAsync(_user);

        Assert.Equal(new SavedPaymentMethod("card", "visa", "4242", 12, 2030), card);
    }

    [Fact]
    public async Task EnsureCustomer_KeepsAnExistingDirectCustomer()
    {
        _service.Enqueue(HttpStatusCode.OK, """{"customer_ref":"user:x","stripe_customer_id":"cus_new","created":true}""");
        _service.Enqueue(HttpStatusCode.OK, """{"customer_ref":"user:x","stripe_customer_id":"cus_new","created":false}""");
        var fresh = new User { Id = Guid.NewGuid(), Email = "a@example.com" };
        var existing = new User { Id = Guid.NewGuid(), Email = "b@example.com", StripeCustomerId = "cus_old" };

        await Gateway().EnsureCustomerAsync(fresh);
        await Gateway().EnsureCustomerAsync(existing);

        Assert.Equal("cus_new", fresh.StripeCustomerId);
        Assert.Equal("cus_old", existing.StripeCustomerId);
        Assert.Equal(HttpMethod.Put, _service.Requests[0].Request.Method);
        Assert.False(Body(0).TryGetProperty("locale", out _));
    }

    [Fact]
    public async Task Invoices_OfAnUnknownCustomer_AreEmpty()
    {
        _service.EnqueueProblem(HttpStatusCode.NotFound, "customer_not_found");
        _service.Enqueue(HttpStatusCode.OK, """
            {"data":[{"id":"in_1","number":"A-1","status":"paid","currency":"eur","total":1500,"amount_due":1500,"amount_paid":1500,
              "created_at":"2026-10-01T10:00:00Z","due_at":null,"hosted_invoice_url":"https://invoice.test/1","invoice_pdf":null}],"next_cursor":null}
            """);

        Assert.Empty(await Gateway().ListInvoicesAsync(_user, 10));
        var invoice = Assert.Single(await Gateway().ListInvoicesAsync(_user, 10));

        Assert.Equal(new InvoiceSummary("A-1", 1500, "paid", "https://invoice.test/1", new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)), invoice);
        Assert.Contains("customer_ref=user%3A6f9619ff", _service.Requests[0].Request.RequestUri!.Query);
    }
}
