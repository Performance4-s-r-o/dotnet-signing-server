using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>
/// #79: a 2FA e-mail while the backoffice is stuck. The item waits in the outbox, goes out
/// exactly once when the service is back (every attempt carries the same Idempotency-Key, so a
/// message the service took but never answered is not created twice), and is never sent once
/// the code is no longer valid (10 minutes). There is no second route out: the break-glass
/// sender is gone (#162).
/// </summary>
public class StuckBackofficeEmailTests
{
    private static OutboxTestHost Host() => new(configure: services =>
        services.AddSingleton<IOutboxHandler, EmailRawOutboxHandler>());

    private static Task<Guid> QueueTwoFactorAsync(OutboxTestHost host)
    {
        var payload = BackofficeOutboxEmailSender.BuildPayload(
            "user@example.com", "Your code", "<p>493817</p>",
            new EmailSendOptions(EmailTemplateId.TwoFactorCode, "en", Guid.NewGuid()));
        Assert.True(payload.Critical);
        return host.EnqueueAsync(payload, critical: true, kind: BackofficeOutboxEmailSender.OutboxKind);
    }

    private static string KeyOf((HttpRequestMessage Request, string Body) r) =>
        r.Request.Headers.GetValues("Idempotency-Key").Single();

    [Fact]
    public async Task Stuck_service_then_back_within_the_validity_sends_exactly_once()
    {
        using var host = Host();
        var id = await QueueTwoFactorAsync(host);

        // Service stuck: a 503, then a timeout.
        host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");
        await host.Processor.DispatchDueAsync(CancellationToken.None);
        host.Time.Advance(TimeSpan.FromSeconds(5));
        host.Service.Responses.Enqueue(async _ =>
        {
            await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token);
            throw new InvalidOperationException("unreachable");
        });
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var waiting = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, waiting.Status);
        Assert.NotNull(waiting.PayloadProtected);

        // Back after 30 s: accepted.
        host.Time.Advance(TimeSpan.FromSeconds(30));
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"em_1","status":"queued"}""");
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(BackofficeOutboxStatus.Sent, (await host.ItemAsync(id)).Status);
        Assert.Equal(3, host.Service.Requests.Count);
        Assert.All(host.Service.Requests, r => Assert.Equal(id.ToString(), KeyOf(r)));

        // Nothing is due any more, however long the dispatcher keeps running.
        host.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));
        Assert.Equal(3, host.Service.Requests.Count);
    }

    [Fact]
    public async Task A_message_the_service_took_but_never_answered_is_one_message_with_one_key()
    {
        using var host = Host();
        var id = await QueueTwoFactorAsync(host);

        // First attempt: the service accepted it, the answer was lost.
        host.Service.Responses.Enqueue(async _ =>
        {
            await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token);
            throw new InvalidOperationException("unreachable");
        });
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        // The retry repeats the key; the service answers the original (200/202) and the item ends.
        host.Time.Advance(TimeSpan.FromSeconds(5));
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"em_1","status":"queued"}""");
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(BackofficeOutboxStatus.Sent, (await host.ItemAsync(id)).Status);
        Assert.Equal(2, host.Service.Requests.Count);
        Assert.Single(host.Service.Requests.Select(KeyOf).Distinct());
        Assert.Equal(host.Service.Requests[0].Body, host.Service.Requests[1].Body);
    }

    [Fact]
    public async Task Service_still_stuck_after_the_code_expired_sends_nothing_late()
    {
        using var host = Host();
        var id = await QueueTwoFactorAsync(host);

        // Stuck past the 10 minutes of the code.
        for (var i = 0; i < 3; i++)
        {
            host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");
            await host.Processor.DispatchDueAsync(CancellationToken.None);
            host.Time.Advance(TimeSpan.FromMinutes(i == 0 ? 1 : 4));
        }
        host.Time.Advance(TimeSpan.FromMinutes(6));

        // The service is back, but the code is stale: the item is closed without a request.
        var before = host.Service.Requests.Count;
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"em_late","status":"queued"}""");
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(before, host.Service.Requests.Count);
        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Null(item.PayloadProtected);
        Assert.Contains("expiry", item.LastError);
    }

    [Fact]
    public async Task A_retry_that_would_come_after_the_expiry_ends_the_item_at_once()
    {
        using var host = Host();
        var id = await QueueTwoFactorAsync(host);

        // Attempts at 0 s, 5 s, 35 s, 2 min 35 s; the next one would be 10 minutes later.
        var steps = new[] { 5, 30, 120 };
        foreach (var seconds in steps)
        {
            host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");
            await host.Processor.DispatchDueAsync(CancellationToken.None);
            host.Time.Advance(TimeSpan.FromSeconds(seconds));
        }
        host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Null(item.PayloadProtected);
        Assert.Equal(4, host.Service.Requests.Count);
    }
}
