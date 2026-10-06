using System.Diagnostics;
using System.Net;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class BackofficeOutboxDispatcherTests
{
    [Fact]
    public async Task Accepted_SendsPayloadWithIdempotencyKeyAndBearer_ThenMarksSent()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { Email = "a@example.com" });
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"em_42","status":"queued"}""");

        Assert.Equal(1, await host.Processor.DispatchDueAsync(CancellationToken.None));

        var (request, body) = Assert.Single(host.Service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(OutboxTestHost.BaseUrl + "v1/test"), request.RequestUri);
        Assert.Equal(id.ToString(), Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(OutboxTestHost.SecretKey, request.Headers.Authorization?.Parameter);
        Assert.Equal("""{"email":"a@example.com"}""", body);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("em_42", item.RemoteId);
        Assert.Null(item.PayloadProtected);
        Assert.Null(item.LockedUntil);
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public async Task ServiceUnavailable_IsRetriedWithTheSameIdempotencyKey()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(host.Time.Now.AddSeconds(5), item.NextAttemptAt);
        Assert.Equal("HTTP 503 unavailable", item.LastError);
        Assert.NotNull(item.PayloadProtected);

        // Not due yet: nothing is sent.
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));

        host.Time.Advance(TimeSpan.FromSeconds(5));
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(2, host.Service.Requests.Count);
        Assert.All(host.Service.Requests, r => Assert.Equal(id.ToString(), r.Request.Headers.GetValues("Idempotency-Key").Single()));
        Assert.Equal(BackofficeOutboxStatus.Sent, (await host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task Unauthorized_BlocksTheItem_AndRequeueSendsItAgain()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.Unauthorized, "invalid_api_key");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var blocked = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Blocked, blocked.Status);
        Assert.NotNull(blocked.PayloadProtected);

        // Still blocked a day later: never retried on its own.
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));

        var requeued = await host.WithDbAsync(db => OutboxHealth.RequeueBlockedAsync(db, host.Time.Now));
        Assert.Equal(new[] { id }, requeued);
        var pending = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);

        await host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Sent, (await host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task UnprocessableEntity_IsDead()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, "validation_failed");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Equal("HTTP 422 validation_failed", item.LastError);
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SuppressedRecipient_IsDead()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, "suppressed_recipient");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(BackofficeOutboxStatus.Dead, (await host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task IdempotencyConflictForTheSameRequest_CountsAsSent()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.Conflict, "idempotency_replayed");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Null(item.PayloadProtected);
    }

    [Fact]
    public async Task Timeout_IsRetried()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.Responses.Enqueue(async _ =>
        {
            await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token);
            throw new InvalidOperationException("unreachable");
        });

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal("Timeout", item.LastError);
    }

    [Fact]
    public async Task NetworkError_IsRetried()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.Responses.Enqueue(_ => throw new HttpRequestException("Connection refused"));

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.StartsWith("Network: ", item.LastError);
    }

    [Fact]
    public async Task UnknownKind_IsKeptForLater()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 }, kind: "not.handled");

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Contains("No handler", item.LastError);
        Assert.Empty(host.Service.Requests);
    }

    [Fact]
    public async Task CriticalItems_GoFirst()
    {
        using var host = new OutboxTestHost();
        var normal = await host.EnqueueAsync(new { n = 1 });
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var critical = await host.EnqueueAsync(new { n = 2 }, critical: true);

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.Equal(
            new[] { critical.ToString(), normal.ToString() },
            host.Service.Requests.Select(r => r.Request.Headers.GetValues("Idempotency-Key").Single()));
    }

    [Fact]
    public async Task TryDispatchNow_ReturnsTheOutcome()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.Enqueue(HttpStatusCode.Created, """{"id":"tk_9"}""");

        var result = await host.Processor.TryDispatchNowAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.True(result.IsSent);
        Assert.Equal("tk_9", result.RemoteId);
    }

    [Fact]
    public async Task TryDispatchNow_DoesNotSendAnItemSomeoneElseHolds()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        await host.WithDbAsync(async db =>
        {
            var item = await db.BackofficeOutboxItems.SingleAsync(i => i.Id == id);
            item.LockedUntil = host.Time.Now.AddMinutes(2);
            return await db.SaveChangesAsync();
        });

        var result = await host.Processor.TryDispatchNowAsync(id, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(result.Attempted);
        Assert.Equal(BackofficeOutboxStatus.Pending, result.Status);
        Assert.Empty(host.Service.Requests);
    }

    [Fact]
    public async Task TryDispatchNow_UnknownItem_ReturnsNoStatus()
    {
        using var host = new OutboxTestHost();

        var result = await host.Processor.TryDispatchNowAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(result.Status);
        Assert.False(result.Attempted);
    }

    [Fact]
    public async Task RepeatedServiceFailures_MarkTheServiceDown()
    {
        using var host = new OutboxTestHost();
        for (var i = 0; i < 3; i++)
        {
            await host.EnqueueAsync(new { n = i });
            host.Service.EnqueueProblem(HttpStatusCode.BadGateway, "bad_gateway");
        }

        await host.Processor.DispatchDueAsync(CancellationToken.None);

        Assert.True(host.Services.GetRequiredService<OutboxCircuitBreaker>().IsServiceDown);
    }

    [Fact]
    public async Task Dispatcher_SendsAnItemSoonAfterCommit()
    {
        using var host = new OutboxTestHost();
        // A poll far beyond the test's own patience: within the wait below, nothing but the
        // commit signal can get the item sent. Timing the real 5 s poll against a stopwatch
        // measures how busy the machine is and passes even when the signal never fires.
        var dispatcher = new BackofficeOutboxDispatcher(
            host.Processor, host.Signal, host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Time, NullLogger<BackofficeOutboxDispatcher>.Instance, pollInterval: TimeSpan.FromMinutes(5));
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            // Let the first (empty) pass finish, so the dispatcher is waiting on the signal.
            await Task.Delay(200);
            var id = await host.EnqueueAsync(new { n = 1 });

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30)
                   && (await host.ItemAsync(id)).Status != BackofficeOutboxStatus.Sent)
            {
                await Task.Delay(25);
            }

            Assert.Equal(BackofficeOutboxStatus.Sent, (await host.ItemAsync(id)).Status);
            // "Soon" is still part of the contract: a signal that eventually limps through is a
            // regression. Loose enough that a busy machine does not fail it, tight enough that
            // it could not be the five-minute poll.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Health_ReportsCountsAndAlerts()
    {
        using var host = new OutboxTestHost();
        var id = await host.EnqueueAsync(new { n = 1 });
        host.Service.EnqueueProblem(HttpStatusCode.Forbidden, "insufficient_scope");
        await host.Processor.DispatchDueAsync(CancellationToken.None);
        await host.EnqueueAsync(new { n = 2 });

        var snapshot = await host.WithDbAsync(db => OutboxHealth.ReadAsync(db));

        Assert.Equal(1, snapshot.Count(BackofficeOutboxStatus.Blocked));
        Assert.Equal(1, snapshot.Count(BackofficeOutboxStatus.Pending));
        Assert.Equal("HTTP 403 insufficient_scope", snapshot.LastError);
        Assert.Empty(OutboxHealth.Alerts(snapshot, host.Time.Now));
        Assert.Single(OutboxHealth.Alerts(snapshot, host.Time.Now.AddHours(2)));
        _ = id;
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyOldFinishedItems()
    {
        using var host = new OutboxTestHost();
        var sent = await host.EnqueueAsync(new { n = 1 });
        await host.Processor.DispatchDueAsync(CancellationToken.None);
        var pending = await host.EnqueueAsync(new { n = 2 }, kind: "not.handled");

        Assert.Equal(0, await host.WithDbAsync(db => OutboxHealth.CleanupAsync(db, host.Time.Now.AddDays(29))));
        Assert.Equal(1, await host.WithDbAsync(db => OutboxHealth.CleanupAsync(db, host.Time.Now.AddDays(31))));

        var left = await host.WithDbAsync(db => db.BackofficeOutboxItems.Select(i => i.Id).ToListAsync());
        Assert.Equal(new[] { pending }, left);
        _ = sent;
    }

    [Fact]
    public async Task LongBacklog_StopsDrainingAfterTheBudget_SoTheAlertStillRuns()
    {
        using var host = new OutboxTestHost();
        for (var i = 0; i < OutboxClaim.BatchSize * 2 + 10; i++)
        {
            await host.EnqueueAsync(new { n = i });
            // A slow but working service: each attempt moves the clock on.
            host.Service.Responses.Enqueue(_ =>
            {
                host.Time.Advance(TimeSpan.FromMinutes(5));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            });
        }
        var logger = new ListLogger<BackofficeOutboxDispatcher>();
        var dispatcher = new BackofficeOutboxDispatcher(
            host.Processor, host.Signal, host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Time, logger);

        var moreDue = await dispatcher.RunPassAsync(CancellationToken.None);

        // One full batch used up the budget; the rest waits for the next pass...
        Assert.True(moreDue);
        Assert.Equal(OutboxClaim.BatchSize, host.Service.Requests.Count);
        // ...and the monitor ran in between, reporting the pending item older than 1 h.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Oldest pending"));

        // Later passes pick up where it stopped until nothing is left.
        Assert.True(await dispatcher.RunPassAsync(CancellationToken.None));
        Assert.False(await dispatcher.RunPassAsync(CancellationToken.None));
        Assert.Equal(OutboxClaim.BatchSize * 2 + 10, host.Service.Requests.Count);
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
