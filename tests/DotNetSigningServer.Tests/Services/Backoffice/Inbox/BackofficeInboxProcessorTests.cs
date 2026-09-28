using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

public class BackofficeInboxProcessorTests
{
    private static InboxTestHost Host(RecordingEventHandler handler) =>
        new(services: s => s.AddSingleton<IBackofficeEventHandler>(handler));

    [Fact]
    public async Task HandlerGetsTheEvent_AndItIsMarkedProcessed()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.PriceScheduled);
        using var host = Host(handler);
        await host.AddAsync("msg_1", BackofficeEventTypes.PriceScheduled, """{"version":3}""");

        Assert.Equal(1, await host.Processor.ProcessDueAsync(CancellationToken.None));

        var evt = Assert.Single(handler.Received);
        Assert.Equal("msg_1", evt.Id);
        Assert.Equal(3, evt.Data.GetProperty("version").GetInt32());
        var item = Assert.Single(await host.ItemsAsync());
        Assert.NotNull(item.ProcessedAt);
        Assert.Null(item.NextAttemptAt);
        Assert.Equal(1, item.Attempts);

        Assert.Equal(0, await host.Processor.ProcessDueAsync(CancellationToken.None));
        Assert.Single(handler.Received);
    }

    [Fact]
    public async Task UnknownType_IsMarkedProcessedWithoutAHandler()
    {
        using var host = new InboxTestHost();
        await host.AddAsync("msg_1", "consent.granted");

        await host.Processor.ProcessDueAsync(CancellationToken.None);

        Assert.NotNull(Assert.Single(await host.ItemsAsync()).ProcessedAt);
    }

    [Fact]
    public async Task WebhookTest_IsLoggedAndMarkedProcessed()
    {
        using var host = new InboxTestHost();
        await host.AddAsync("msg_test", BackofficeEventTypes.WebhookTest);

        await host.Processor.ProcessDueAsync(CancellationToken.None);

        var item = Assert.Single(await host.ItemsAsync());
        Assert.NotNull(item.ProcessedAt);
        Assert.Null(item.Error);
    }

    [Fact]
    public async Task FailingHandler_IsRetriedWithBackoff()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.PriceEffective) { Fail = _ => new InvalidOperationException("boom") };
        using var host = Host(handler);
        await host.AddAsync("msg_1", BackofficeEventTypes.PriceEffective);

        await host.Processor.ProcessDueAsync(CancellationToken.None);

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Null(item.ProcessedAt);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(host.Time.Now + TimeSpan.FromSeconds(30), item.NextAttemptAt);
        Assert.Contains("boom", item.Error);

        // Not due yet.
        Assert.Equal(0, await host.Processor.ProcessDueAsync(CancellationToken.None));

        handler.Fail = _ => null;
        host.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await host.Processor.ProcessDueAsync(CancellationToken.None));

        item = Assert.Single(await host.ItemsAsync());
        Assert.NotNull(item.ProcessedAt);
        Assert.Null(item.Error);
        Assert.Equal(2, item.Attempts);
    }

    [Fact]
    public async Task FailingHandler_EmailAddressesAreMaskedInTheStoredError()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.EmailBounced)
        {
            Fail = _ => new InvalidOperationException("no user for jan.novak+test@example.co.uk (bounce to Info@Firma.cz)"),
        };
        using var host = Host(handler);
        await host.AddAsync("msg_1", BackofficeEventTypes.EmailBounced);

        await host.Processor.ProcessDueAsync(CancellationToken.None);

        var error = Assert.Single(await host.ItemsAsync()).Error;
        Assert.Equal("InvalidOperationException: no user for [email] (bounce to [email])", error);
    }

    [Fact]
    public void ErrorText_IsTruncated()
    {
        var text = BackofficeInboxProcessor.ErrorText(new Exception(new string('x', 2000)));

        Assert.Equal(512, text.Length);
        Assert.StartsWith("Exception: xxx", text);
    }

    [Fact]
    public async Task AfterMaxAttempts_ProcessingStops()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.EmailBounced) { Fail = _ => new Exception("down") };
        using var host = Host(handler);
        await host.AddAsync("msg_1", BackofficeEventTypes.EmailBounced);

        for (var i = 0; i < BackofficeInboxProcessor.MaxAttempts + 3; i++)
        {
            await host.Processor.ProcessDueAsync(CancellationToken.None);
            host.Time.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(BackofficeInboxProcessor.MaxAttempts, handler.Received.Count);
        var item = Assert.Single(await host.ItemsAsync());
        Assert.Null(item.ProcessedAt);
        Assert.Null(item.NextAttemptAt);
        Assert.Equal(BackofficeInboxProcessor.MaxAttempts, item.Attempts);
    }

    [Fact]
    public async Task Items_AreProcessedOldestFirst()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.DocumentPublished);
        using var host = Host(handler);
        await host.AddAsync("msg_b", BackofficeEventTypes.DocumentPublished);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.AddAsync("msg_a", BackofficeEventTypes.DocumentPublished);

        await host.Processor.ProcessDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "msg_b", "msg_a" }, handler.Received.Select(e => e.Id));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(7, 1920)]
    [InlineData(8, 3600)]
    [InlineData(19, 3600)]
    public void RetryDelay_GrowsFrom30SecondsToAnHour(int failedAttempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackofficeInboxProcessor.RetryDelay(failedAttempts));
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyOldProcessedItems()
    {
        using var host = new InboxTestHost();
        await host.AddAsync("msg_done", "consent.granted");
        await host.AddAsync("msg_pending", BackofficeEventTypes.PriceScheduled);
        await host.WithDbAsync(async db =>
        {
            var done = db.BackofficeWebhookInboxItems.Single(i => i.WebhookId == "msg_done");
            done.ProcessedAt = host.Time.Now;
            return await db.SaveChangesAsync();
        });

        var now = host.Time.Now + BackofficeInboxProcessor.Retention + TimeSpan.FromDays(1);
        Assert.Equal(1, await host.WithDbAsync(db => BackofficeInboxProcessor.DeleteProcessedAsync(db, now)));

        Assert.Equal("msg_pending", Assert.Single(await host.ItemsAsync()).WebhookId);
    }
}

public class EventHandlerRegistryTests
{
    private static readonly LoggingBackofficeEventHandler Fallback = new(NullLogger<LoggingBackofficeEventHandler>.Instance);

    [Fact]
    public void EverySubscribedType_HasAHandler()
    {
        var registry = new EventHandlerRegistry(Array.Empty<IBackofficeEventHandler>(), Fallback);

        Assert.All(BackofficeEventTypes.Subscribed, t => Assert.Same(Fallback, registry.Find(t)));
        Assert.Same(Fallback, registry.Find(BackofficeEventTypes.WebhookTest));
        Assert.Null(registry.Find("consent.granted"));
    }

    [Fact]
    public void ModuleHandler_TakesOverFromTheFallback()
    {
        var pricing = new RecordingEventHandler(BackofficeEventTypes.PriceScheduled, BackofficeEventTypes.PriceEffective);

        var registry = new EventHandlerRegistry(new IBackofficeEventHandler[] { pricing }, Fallback);

        Assert.Same(pricing, registry.Find(BackofficeEventTypes.PriceScheduled));
        Assert.Same(Fallback, registry.Find(BackofficeEventTypes.PriceUnscheduled));
    }

    [Fact]
    public void TwoHandlersForOneType_IsAWiringError()
    {
        var a = new RecordingEventHandler(BackofficeEventTypes.EmailBounced);
        var b = new RecordingEventHandler(BackofficeEventTypes.EmailBounced);

        Assert.Throws<InvalidOperationException>(() => new EventHandlerRegistry(new IBackofficeEventHandler[] { a, b }, Fallback));
    }
}
