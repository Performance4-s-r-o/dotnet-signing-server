using System.Net;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Ops;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>Billing client under outages: circuit breaker, fail-closed refusals, invoice cache.</summary>
public class BillingResilienceTests
{
    private readonly StubServiceHandler _service = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAlerter _alerts = new();

    private sealed class RecordingAlerter : IOpsAlerter
    {
        public List<string> Keys { get; } = [];
        public void Alert(string key, string text) => Keys.Add(key);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://backoffice.example.com/") };
    }

    private BackofficeBillingClient Client() =>
        new(new Factory(_service), NullLogger<BackofficeBillingClient>.Instance, _ => TimeSpan.Zero, _time, _alerts);

    private void Outages(int count)
    {
        for (var i = 0; i < count; i++) _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
    }

    [Fact]
    public async Task AfterRepeatedOutages_CallsAreNotSent_AndNeverLookSuccessful()
    {
        var client = Client();
        Outages(BillingCircuitBreaker.Threshold);
        for (var i = 0; i < BillingCircuitBreaker.Threshold; i++)
        {
            await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        }
        Assert.True(client.Breaker.IsOpen);
        Assert.Equal(["billing-api-down"], _alerts.Keys);

        var sent = _service.Requests.Count;
        _service.Enqueue(HttpStatusCode.Created, """{"url":"https://billing.stripe.test/p"}""");
        var refused = await client.CreateCheckoutAsync(new { }, CancellationToken.None);

        Assert.Equal(sent, _service.Requests.Count);
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Status);
        Assert.Throws<BillingApiException>(() => refused.RequireJson("POST checkout"));
    }

    [Fact]
    public async Task ABlockedCharge_IsNoAnswer_NotAFailureOfThePayment()
    {
        var client = Client();
        Outages(BillingCircuitBreaker.Threshold);
        for (var i = 0; i < BillingCircuitBreaker.Threshold; i++) await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        var sent = _service.Requests.Count;

        var charge = await client.CreateChargeAsync(new { }, "key-1", CancellationToken.None);

        Assert.Null(charge.Status);
        Assert.Equal(sent, _service.Requests.Count);
    }

    [Fact]
    public async Task Breaker_ClosesAfterItsPause_AndAnAnswerKeepsItClosed()
    {
        var client = Client();
        Outages(BillingCircuitBreaker.Threshold);
        for (var i = 0; i < BillingCircuitBreaker.Threshold; i++) await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        Assert.True(client.Breaker.IsOpen);

        _time.Advance(BillingCircuitBreaker.OpenFor + TimeSpan.FromSeconds(1));
        Assert.False(client.Breaker.IsOpen);
        _service.Enqueue(HttpStatusCode.Created, """{"url":"https://billing.stripe.test/p"}""");
        var ok = await client.CreateCheckoutAsync(new { }, CancellationToken.None);

        Assert.True(ok.IsSuccess);
        Assert.False(client.Breaker.IsOpen);
    }

    [Fact]
    public async Task Refusals_DoNotOpenTheBreaker()
    {
        var client = Client();
        for (var i = 0; i < BillingCircuitBreaker.Threshold + 2; i++)
        {
            _service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, "validation_failed");
            await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        }
        Assert.False(client.Breaker.IsOpen);
        Assert.Empty(_alerts.Keys);
    }

    [Fact]
    public async Task Invoices_AreServedFreshFromTheCache_ThenStaleOnlyDuringAnOutage()
    {
        var client = Client();
        _service.Enqueue(HttpStatusCode.OK, """{"data":[{"number":"A-1"}]}""");
        var first = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        var again = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        Assert.Single(_service.Requests);
        Assert.Same(first, again);

        // Past the fresh window the service is asked again; an outage then falls back to the old list.
        _time.Advance(BackofficeBillingClient.InvoicesFresh + TimeSpan.FromSeconds(1));
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        var stale = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        Assert.Equal(2, _service.Requests.Count);
        Assert.Same(first, stale);

        // A refusal is a real answer, not an outage: no stale copy.
        _service.EnqueueProblem(HttpStatusCode.Forbidden, "forbidden");
        var refused = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        Assert.Equal(403, refused.Status);
    }

    [Fact]
    public async Task Invoices_AreNotServedStaleForever_NorToAnotherCustomer()
    {
        var client = Client();
        _service.Enqueue(HttpStatusCode.OK, """{"data":[{"number":"A-1"}]}""");
        await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);

        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        var other = await client.ListInvoicesAsync("user:2", 10, CancellationToken.None);
        Assert.Equal(502, other.Status);

        _time.Advance(BackofficeBillingClient.InvoicesStale + TimeSpan.FromSeconds(1));
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        var tooOld = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        Assert.Equal(502, tooOld.Status);
    }

    [Fact]
    public async Task CheckoutAndCustomerReads_AreNeverCached()
    {
        var client = Client();
        _service.Enqueue(HttpStatusCode.OK, """{"id":"cs_1","payment_status":"unpaid"}""");
        _service.Enqueue(HttpStatusCode.OK, """{"id":"cs_1","payment_status":"paid"}""");
        var a = await client.GetCheckoutAsync("cs_1", CancellationToken.None);
        var b = await client.GetCheckoutAsync("cs_1", CancellationToken.None);
        Assert.Equal(2, _service.Requests.Count);
        Assert.NotEqual(a.Body, b.Body);

        _service.Enqueue(HttpStatusCode.OK, """{"card":{"type":"card"}}""");
        await client.GetCustomerAsync("user:1", CancellationToken.None);
        _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
        var outage = await client.GetCustomerAsync("user:1", CancellationToken.None);
        Assert.Equal(502, outage.Status);
    }

    [Fact]
    public async Task SlowOrFailingReads_NeverCloseCheckoutAndCharges()
    {
        var client = Client();
        for (var i = 0; i < BillingCircuitBreaker.Threshold; i++)
        {
            _service.EnqueueProblem(HttpStatusCode.BadGateway, "stripe_error");
            await client.GetCheckoutAsync("cs_1", CancellationToken.None);
        }
        Assert.True(client.ReadBreaker.IsOpen);
        Assert.False(client.Breaker.IsOpen);

        _service.Enqueue(HttpStatusCode.Created, """{"url":"https://billing.stripe.test/p"}""");
        var checkout = await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        Assert.True(checkout.IsSuccess);
        Assert.Contains("billing-api-reads-down", _alerts.Keys);
        Assert.DoesNotContain("billing-api-down", _alerts.Keys);
    }

    [Fact]
    public async Task WriteOutages_DoNotBlockReads()
    {
        var client = Client();
        Outages(BillingCircuitBreaker.Threshold);
        for (var i = 0; i < BillingCircuitBreaker.Threshold; i++) await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        Assert.True(client.Breaker.IsOpen);

        _service.Enqueue(HttpStatusCode.OK, """{"id":"cs_1","payment_status":"paid"}""");
        Assert.True((await client.GetCheckoutAsync("cs_1", CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task RepeatWithOpenBreaker_ReturnsAtOnce_AndStaysUnknown()
    {
        var waited = new List<int>();
        var client = new BackofficeBillingClient(new Factory(_service), NullLogger<BackofficeBillingClient>.Instance,
            a => { waited.Add(a); return TimeSpan.FromMinutes(5); }, _time, _alerts);
        Outages(BillingCircuitBreaker.Threshold);
        for (var i = 0; i < BillingCircuitBreaker.Threshold - 1; i++) await client.CreateCheckoutAsync(new { }, CancellationToken.None);
        waited.Clear();

        // The charge's first attempt fails and opens the breaker; the repeat must not wait 5 minutes.
        var charge = await client.CreateChargeAsync(new { }, "key-1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(502, charge.Status);
        Assert.Empty(waited);
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") };
        }
    }

    [Fact]
    public async Task OnlyTheInvoiceList_GetsTheShortTimeout()
    {
        var client = new BackofficeBillingClient(new Factory(new SlowHandler(TimeSpan.FromMilliseconds(400))),
            NullLogger<BackofficeBillingClient>.Instance, _ => TimeSpan.Zero, _time, _alerts)
        { InvoicesTimeout = TimeSpan.FromMilliseconds(80) };

        var invoices = await client.ListInvoicesAsync("user:1", 10, CancellationToken.None);
        var checkout = await client.GetCheckoutAsync("cs_1", CancellationToken.None);

        Assert.Null(invoices.Status);
        Assert.True(checkout.IsSuccess);
    }
}
