using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

public class BackofficePollingServiceTests
{
    private static string Page(bool hasMore, string cursor, bool clamped = false, params (string Id, string Type)[] events) =>
        JsonSerializer.Serialize(new
        {
            data = events.Select(e => new
            {
                id = e.Id,
                event_id = Guid.NewGuid(),
                type = e.Type,
                timestamp = "2026-09-28T11:00:00Z",
                data = new { version = 1, source = e.Id },
            }),
            next_cursor = cursor,
            has_more = hasMore,
            window_clamped = clamped,
        });

    private static Dictionary<string, string> Query(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task ReadsEveryPage_StoresEventsAndTheCursor()
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(true, "cur_1", false, ("msg_1", "price.scheduled"), ("msg_2", "document.published")));
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_2", false, ("msg_3", "email.bounced")));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new BackofficePollResult(2, 3, 3, false, true), result);
        var items = await host.ItemsAsync();
        Assert.Equal(new[] { "msg_1", "msg_2", "msg_3" }, items.Select(i => i.WebhookId).Order());
        Assert.All(items, i => Assert.Equal(BackofficeInboxSource.Poll, i.Source));
        Assert.Contains("\"source\":\"msg_1\"", items.Single(i => i.WebhookId == "msg_1").PayloadJson);
        Assert.Equal("cur_2", await host.CursorAsync());
        Assert.True(await host.Signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        // Second page continues from the first page's cursor.
        Assert.Equal("cur_1", Query(host.Service.Requests[1].Request)["since"]);
    }

    [Fact]
    public async Task FirstRun_StartsOneDayBack_AndAsksForTheSubscribedTypes()
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_1"));

        await host.Polling.PollOnceAsync(CancellationToken.None);

        var (request, _) = Assert.Single(host.Service.Requests);
        Assert.Equal("/v1/events", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        var query = Query(request);
        Assert.Equal("2026-09-27T12:00:00Z", query["since"]);
        Assert.Equal(string.Join(",", BackofficeEventTypes.Subscribed), query["types"]);
        Assert.Equal("100", query["limit"]);
    }

    [Fact]
    public async Task EmptyPage_StillStoresTheCursor()
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_empty"));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, result.Events);
        Assert.Equal("cur_empty", await host.CursorAsync());
        Assert.False(await host.Signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task CursorSurvivesARestart()
    {
        var dbName = "polling-" + Guid.NewGuid();
        using (var first = new InboxTestHost(database: o => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(o, dbName)))
        {
            first.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_saved", false, ("msg_1", "price.scheduled")));
            await first.Polling.PollOnceAsync(CancellationToken.None);
        }

        using var second = new InboxTestHost(database: o => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(o, dbName));
        second.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_next"));
        await second.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal("cur_saved", Query(Assert.Single(second.Service.Requests).Request)["since"]);
        Assert.Equal("cur_next", await second.CursorAsync());
    }

    [Fact]
    public async Task EventAlreadyReceivedByWebhook_IsNotDuplicated_AndAMissedOneIsAdded()
    {
        using var host = new InboxTestHost();
        Assert.True(await host.AddAsync("msg_hook", "price.scheduled"));
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_1", false, ("msg_hook", "price.scheduled"), ("msg_missed", "price.effective")));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Added);
        var items = await host.ItemsAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(BackofficeInboxSource.Webhook, items.Single(i => i.WebhookId == "msg_hook").Source);
        Assert.Equal(BackofficeInboxSource.Poll, items.Single(i => i.WebhookId == "msg_missed").Source);
    }

    [Fact]
    public async Task WindowClamped_RunsEveryResyncOnce()
    {
        var docs = new RecordingResync("docs");
        var pricing = new RecordingResync("pricing");
        using var host = new InboxTestHost(services: s =>
        {
            s.AddSingleton<IBackofficeResync>(docs);
            s.AddSingleton<IBackofficeResync>(pricing);
        });
        host.Service.Enqueue(HttpStatusCode.OK, Page(true, "cur_1", true, ("msg_1", "price.scheduled")));
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_2", true));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.True(result.WindowClamped);
        Assert.Equal(1, docs.Calls);
        Assert.Equal(1, pricing.Calls);
        Assert.Equal("cur_2", await host.CursorAsync());
    }

    [Fact]
    public async Task FailedResync_KeepsTheOldCursorSoItIsRetried()
    {
        var resync = new RecordingResync("docs") { Fail = true };
        using var host = new InboxTestHost(services: s => s.AddSingleton<IBackofficeResync>(resync));
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_old", host.Time.Now);
            return 0;
        });
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_new", true, ("msg_1", "price.scheduled")));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal("cur_old", await host.CursorAsync());
        Assert.Single(await host.ItemsAsync()); // the page's events are kept; a re-poll dedupes them
    }

    [Fact]
    public async Task WindowClamped_WithoutAnyResync_StillMovesOn()
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_1", true));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("cur_1", await host.CursorAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ErrorResponse_StopsTheRunAndKeepsTheCursor(HttpStatusCode status)
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(true, "cur_1", false, ("msg_1", "price.scheduled")));
        host.Service.Enqueue(status, """{"code":"x"}""");

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(1, result.Pages);
        Assert.Equal("cur_1", await host.CursorAsync());
    }

    [Fact]
    public async Task NetworkError_IsNotThrown()
    {
        using var host = new InboxTestHost();
        host.Service.Responses.Enqueue(_ => throw new HttpRequestException("connection refused"));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Null(await host.CursorAsync());
    }

    [Fact]
    public async Task InvalidEvents_AreSkipped()
    {
        using var host = new InboxTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, Page(false, "cur_1", false, ("", "price.scheduled"), (new string('x', 200), "price.scheduled"), ("msg_ok", "price.scheduled")));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Added);
        Assert.Equal("msg_ok", Assert.Single(await host.ItemsAsync()).WebhookId);
    }

    [Fact]
    public async Task EndlessHasMore_StopsAfterThePageCap()
    {
        using var host = new InboxTestHost();
        for (var i = 0; i < BackofficePollingService.MaxPagesPerRun + 5; i++)
            host.Service.Enqueue(HttpStatusCode.OK, Page(true, "cur_" + i));

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.Equal(BackofficePollingService.MaxPagesPerRun, result.Pages);
    }

    [Fact]
    public void Interval_IsShorterWithoutWebhooks()
    {
        using var withWebhooks = new InboxTestHost();
        using var withoutWebhooks = new InboxTestHost(configure: o => o.Webhook.Secret = null);

        Assert.Equal(TimeSpan.FromMinutes(15), withWebhooks.Polling.Interval);
        Assert.Equal(TimeSpan.FromMinutes(2), withoutWebhooks.Polling.Interval);
    }

    private sealed class RecordingResync(string name) : IBackofficeResync
    {
        public string Name => name;
        public int Calls { get; private set; }
        public bool Fail { get; init; }

        public Task ResyncAsync(string reason, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail ? Task.FromException(new InvalidOperationException("source down")) : Task.CompletedTask;
        }
    }
}
