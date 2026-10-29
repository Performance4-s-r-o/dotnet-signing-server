using System.Net;
using DotNetSigningServer.Services.Ops;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Ops;

public class OpsAlerterTests
{
    private readonly StubServiceHandler _hook = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private OpsAlerter Alerter(string? url = "https://hooks.example.com/t/abc") =>
        new(new Factory(_hook), Microsoft.Extensions.Options.Options.Create(new OpsAlertOptions { WebhookUrl = url }), _time, NullLogger<OpsAlerter>.Instance);

    [Fact]
    public async Task Posts_TextJson_AndDeduplicatesWithinTheCooldown()
    {
        var alerter = Alerter();
        await alerter.PostAsync("k", "first");
        Assert.Equal("""{"text":"[signing server] first"}""", _hook.Requests[0].Body);

        alerter.Alert("k", "x");
        alerter.Alert("k", "x");
        await Task.Delay(200);
        Assert.Equal(2, _hook.Requests.Count); // one direct, one from Alert (the second Alert was deduplicated)
        _time.Advance(OpsAlerter.DefaultCooldown + TimeSpan.FromMinutes(1));
        alerter.Alert("k", "x");
        await Task.Delay(200);
        Assert.Equal(3, _hook.Requests.Count);
    }

    [Fact]
    public async Task WithoutAUrl_NothingIsSent()
    {
        Alerter(url: "").Alert("k", "x");
        await Task.Delay(100);
        Assert.Empty(_hook.Requests);
    }

    [Fact]
    public async Task AFailedPost_IsRetriedAfterAShortPause_NotAtOnce_AndNeverThrows()
    {
        var alerter = Alerter();
        _hook.Enqueue(HttpStatusCode.InternalServerError);
        alerter.Alert("k", "x");
        await Task.Delay(200);
        alerter.Alert("k", "x");
        await Task.Delay(100);
        Assert.Single(_hook.Requests);

        _time.Advance(OpsAlerter.FailureCooldown + TimeSpan.FromSeconds(1));
        alerter.Alert("k", "x");
        await Task.Delay(200);
        Assert.Equal(2, _hook.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentAlerts_OfOneKey_SendOnce()
    {
        var alerter = Alerter();
        Parallel.For(0, 64, _ => alerter.Alert("same", "x"));
        await Task.Delay(300);
        Assert.Single(_hook.Requests);
    }

    [Fact]
    public async Task OverTheKeyCap_TheOldestKeyIsDropped()
    {
        var alerter = Alerter();
        for (var i = 0; i < 250; i++)
        {
            alerter.Alert("k" + i, "x");
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        await Task.Delay(500);
        var sent = _hook.Requests.Count;
        alerter.Alert("k249", "x"); // recent: still deduplicated
        alerter.Alert("k0", "x");   // oldest: forgotten, sent again
        await Task.Delay(300);
        Assert.Equal(sent + 1, _hook.Requests.Count);
    }

    [Theory]
    [InlineData("http://hooks.example.com/x")]
    [InlineData("not a url")]
    public void ANonHttpsUrl_RefusesStartup(string url) =>
        Assert.Throws<InvalidOperationException>(() => OpsAlerter.ParseUrl(url));
}
