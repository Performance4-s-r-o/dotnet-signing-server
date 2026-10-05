using System.Text;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

public class BackofficeWebhookControllerTests
{
    private const string Body = """{"type":"price.scheduled","timestamp":"2026-09-28T12:00:00Z","data":{"version":2,"label":"2027"}}""";

    private static async Task<IActionResult> PostAsync(
        InboxTestHost host, string body, string id = "msg_1", string? secret = InboxTestHost.Secret,
        long? timestamp = null, string? signature = null)
    {
        var ts = timestamp ?? host.Time.GetUtcNow().ToUnixTimeSeconds();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["webhook-id"] = id;
        context.Request.Headers["webhook-timestamp"] = ts.ToString();
        context.Request.Headers["webhook-signature"] = signature ?? StandardWebhookVerifier.Sign(secret!, id, ts, body);

        using var scope = host.Services.CreateScope();
        var controller = new BackofficeWebhookController(
            scope.ServiceProvider.GetRequiredService<IOptions<P4BackofficeProductOptions>>(),
            scope.ServiceProvider.GetRequiredService<BackofficeInbox>(),
            host.Signal,
            host.Time,
            NullLogger<BackofficeWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        return await controller.Receive(CancellationToken.None);
    }

    private static int Status(IActionResult result) => Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode ?? 0;

    [Fact]
    public async Task SignedWebhook_Returns200AndStoresTheEvent()
    {
        using var host = new InboxTestHost();

        Assert.Equal(200, Status(await PostAsync(host, Body)));

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal("msg_1", item.WebhookId);
        Assert.Equal("price.scheduled", item.Type);
        Assert.Equal(BackofficeInboxSource.Webhook, item.Source);
        Assert.Contains("\"label\":\"2027\"", item.PayloadJson);
        Assert.Null(item.ProcessedAt);
        Assert.True(await host.Signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task SameWebhookIdAgain_Returns200WithoutANewRow()
    {
        using var host = new InboxTestHost();
        await PostAsync(host, Body);

        Assert.Equal(200, Status(await PostAsync(host, Body)));

        Assert.Single(await host.ItemsAsync());
    }

    [Fact]
    public async Task WrongSignature_Returns400AndStoresNothing()
    {
        using var host = new InboxTestHost();

        var result = await PostAsync(host, Body, secret: InboxTestHost.PreviousSecret);

        Assert.Equal(400, Status(result));
        Assert.Empty(await host.ItemsAsync());
    }

    [Fact]
    public async Task StaleTimestamp_Returns400AndStoresNothing()
    {
        using var host = new InboxTestHost();
        var ts = host.Time.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds();

        Assert.Equal(400, Status(await PostAsync(host, Body, timestamp: ts)));
        Assert.Empty(await host.ItemsAsync());
    }

    [Fact]
    public async Task MissingSignature_Returns400()
    {
        using var host = new InboxTestHost();

        Assert.Equal(400, Status(await PostAsync(host, Body, signature: "")));
        Assert.Empty(await host.ItemsAsync());
    }

    [Fact]
    public async Task DuringRotation_BothSecretsAreAccepted()
    {
        using var host = new InboxTestHost(configure: o => o.Webhook.PreviousSecret = InboxTestHost.PreviousSecret);

        Assert.Equal(200, Status(await PostAsync(host, Body, id: "msg_new", secret: InboxTestHost.Secret)));
        Assert.Equal(200, Status(await PostAsync(host, Body, id: "msg_old", secret: InboxTestHost.PreviousSecret)));

        Assert.Equal(2, (await host.ItemsAsync()).Count);
    }

    [Fact]
    public async Task WithoutWebhookSecret_Returns404()
    {
        using var host = new InboxTestHost(configure: o => o.Webhook.Secret = "");

        Assert.Equal(404, Status(await PostAsync(host, Body)));
        Assert.Empty(await host.ItemsAsync());
    }

    [Theory]
    [InlineData("Off", BackofficeDisabledReason.None)]
    [InlineData("On", BackofficeDisabledReason.PrivateServer)]
    public async Task IntegrationOff_Returns404(string mode, BackofficeDisabledReason reason)
    {
        using var host = new InboxTestHost(configure: o =>
        {
            o.Mode = mode;
            o.DisabledReason = reason;
        });

        Assert.Equal(404, Status(await PostAsync(host, Body)));
        Assert.Empty(await host.ItemsAsync());
    }

    [Fact]
    public async Task OversizedId_Returns400()
    {
        using var host = new InboxTestHost();

        Assert.Equal(400, Status(await PostAsync(host, Body, id: new string('x', 129))));
        Assert.Empty(await host.ItemsAsync());
    }

    [Fact]
    public void Endpoint_IsAnonymousAndOutsideTheApiDocs()
    {
        var type = typeof(BackofficeWebhookController);

        Assert.False(typeof(ApiControllerBase).IsAssignableFrom(type));
        Assert.NotNull(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).SingleOrDefault());
        var method = type.GetMethod(nameof(BackofficeWebhookController.Receive))!;
        var post = (HttpPostAttribute)method.GetCustomAttributes(typeof(HttpPostAttribute), true).Single();
        Assert.Equal("/api/webhooks/p4", post.Template);
        var limit = (RequestSizeLimitAttribute)method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true).Single();
        Assert.NotNull(limit);
    }
}
