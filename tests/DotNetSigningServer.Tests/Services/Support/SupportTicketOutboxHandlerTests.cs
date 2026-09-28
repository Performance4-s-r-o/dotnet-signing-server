using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Support;
using DotNetSigningServer.Tests.Services.Backoffice.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Support;

public class SupportTicketOutboxHandlerTests
{
    private static OutboxTestHost Host() => new(configure: s =>
        s.AddSingleton<IOutboxHandler>(new SupportTicketOutboxHandler(NullLogger<SupportTicketOutboxHandler>.Instance)));

    private static SupportTicketPayload Payload() => SupportTicketRequest.Build(
        new User { Id = Guid.NewGuid(), Email = "jana@example.com" },
        new SupportTicketInput("billing", "Subject", "<b>hi</b>", "normal", "cs"));

    [Fact]
    public async Task Created_IsSentWithTheTicketNumber()
    {
        using var host = Host();
        var id = await host.EnqueueAsync(Payload(), kind: SupportTicketRequest.OutboxKind);
        host.Service.Enqueue(HttpStatusCode.Created,
            """{"id":"5b1c","status":"submitted","ticket_number":"482193","portal_url":"https://help.example.com/view.php"}""");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("5b1c#482193", item.RemoteId);
        Assert.Equal("482193", SupportTicketReceipt.TicketNumber(item.RemoteId));

        var (request, body) = Assert.Single(host.Service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/support/tickets", request.RequestUri!.AbsolutePath);
        Assert.Equal(id.ToString(), request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("<b>hi</b>", json.RootElement.GetProperty("message").GetString());
        Assert.Equal("jana@example.com", json.RootElement.GetProperty("reporter").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Queued_IsSentWithoutATicketNumber()
    {
        using var host = Host();
        var id = await host.EnqueueAsync(Payload(), kind: SupportTicketRequest.OutboxKind);
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"5b1c","status":"queued","ticket_number":null,"portal_url":null}""");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("5b1c", item.RemoteId);
        Assert.Null(SupportTicketReceipt.TicketNumber(item.RemoteId));
    }

    [Fact]
    public async Task SupportInvalid_IsDeadWithAnError()
    {
        var logs = new ListLoggerProvider();
        using var host = new OutboxTestHost(configure: s =>
            s.AddSingleton<IOutboxHandler>(new SupportTicketOutboxHandler(logs.CreateLogger<SupportTicketOutboxHandler>())));
        var id = await host.EnqueueAsync(Payload(), kind: SupportTicketRequest.OutboxKind);
        host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, SupportTicketOutboxHandler.SupportInvalid);

        await host.Processor.DispatchDueAsync(CancellationToken.None);
        host.Time.Advance(TimeSpan.FromDays(1));
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Single(host.Service.Requests);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(SupportTicketOutboxHandler.SupportInvalid));
    }

    [Fact]
    public async Task ServiceDown_IsRetriedWithTheSameKey()
    {
        using var host = Host();
        var id = await host.EnqueueAsync(Payload(), kind: SupportTicketRequest.OutboxKind);
        host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);
        host.Service.Enqueue(HttpStatusCode.Created, """{"id":"5b1c","status":"submitted","ticket_number":"7","portal_url":null}""");

        await host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Pending, (await host.ItemAsync(id)).Status);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("7", SupportTicketReceipt.TicketNumber(item.RemoteId));
        Assert.Equal(2, host.Service.Requests.Count);
        Assert.All(host.Service.Requests, r =>
            Assert.Equal(id.ToString(), r.Request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single()));
    }
}
