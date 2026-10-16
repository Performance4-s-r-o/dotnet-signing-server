using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

[Trait("Category", "Db")]
public class InboxPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public InboxPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private async Task ClearAsync()
    {
        await using var db = _pg.CreateContext();
        await db.BackofficeWebhookInboxItems.ExecuteDeleteAsync();
        await db.BackofficeStates.ExecuteDeleteAsync();
    }

    [DockerFact]
    public async Task Migration_CreatesTheTablesAndIndexes()
    {
        await using var db = _pg.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select indexname from pg_indexes where schemaname = 'dotnet_signing' "
            + "and tablename in ('BackofficeWebhookInboxItems', 'BackofficeStates') order by 1", conn);
        var indexes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));

        Assert.Equal(
            new[]
            {
                "IX_BackofficeWebhookInboxItems_NextAttemptAt",
                "IX_BackofficeWebhookInboxItems_ProcessedAt",
                "IX_BackofficeWebhookInboxItems_WebhookId",
                "PK_BackofficeStates",
                "PK_BackofficeWebhookInboxItems",
            },
            indexes);
    }

    [DockerFact]
    public async Task AddIfNew_InsertsOncePerWebhookId()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);

        Assert.True(await host.AddAsync("msg_1", "price.scheduled", """{"version":1}"""));
        Assert.False(await host.AddAsync("msg_1", "price.scheduled", """{"version":1}""", BackofficeInboxSource.Poll));

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal(BackofficeInboxSource.Webhook, item.Source);
        Assert.Equal(host.Time.Now, item.NextAttemptAt);
        Assert.Equal(0, item.Attempts);
    }

    [DockerFact]
    public async Task ConcurrentDeliveries_OfOneEvent_StoreOneRow()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);

        var added = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            host.AddAsync("msg_race", "price.effective", "{}", i % 2 == 0 ? BackofficeInboxSource.Webhook : BackofficeInboxSource.Poll)));

        Assert.Equal(1, added.Count(a => a));
        Assert.Single(await host.ItemsAsync());
    }

    [DockerFact]
    public async Task Processor_And_Cursor_WorkOnPostgres()
    {
        await ClearAsync();
        var handler = new RecordingEventHandler(BackofficeEventTypes.PriceScheduled);
        using var host = new InboxTestHost(_pg.Configure, services: s => s.AddSingleton<IBackofficeEventHandler>(handler));
        await host.AddAsync("msg_1", BackofficeEventTypes.PriceScheduled, """{"version":7}""");

        Assert.Equal(1, await host.Processor.ProcessDueAsync(CancellationToken.None));
        Assert.Equal(7, Assert.Single(handler.Received).Data.GetProperty("version").GetInt32());
        Assert.NotNull(Assert.Single(await host.ItemsAsync()).ProcessedAt);

        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_1", host.Time.Now);
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_2", host.Time.Now);
            return 0;
        });
        Assert.Equal("cur_2", await host.CursorAsync());
    }

    [DockerFact]
    public async Task Polling_SkipsWhileAnotherInstanceHoldsTheLease_AndReleasesItsOwn()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);
        var leaseKey = BackofficeLease.KeyFor(BackofficePollingService.LeaseName);
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_1", host.Time.Now);
            // Another instance is in the middle of a run (renewed just now).
            await BackofficeStateStore.SetAsync(db, leaseKey, "other-instance", host.Time.Now);
            return 0;
        });

        var skipped = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.True(skipped.Skipped);
        Assert.Empty(host.Service.Requests);
        Assert.Equal("cur_1", await host.CursorAsync());

        // The other instance died: once its lease has expired, this one polls, and gives its own lease back.
        host.Time.Advance(BackofficePollingService.LeaseDuration);
        host.Service.Enqueue(System.Net.HttpStatusCode.OK,
            """{"data":[],"next_cursor":"cur_2","has_more":false,"window_clamped":false}""");
        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(1, result.Pages);
        Assert.Equal("cur_2", await host.CursorAsync());
        Assert.Null(await host.WithDbAsync(db => BackofficeStateStore.GetAsync(db, leaseKey)));
    }

    [DockerFact]
    public async Task Lease_IsExclusive_AndTakenOverOnlyAfterExpiry()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);
        var now = host.Time.Now;
        var duration = TimeSpan.FromMinutes(2);

        await host.WithDbAsync(async db =>
        {
            Assert.True(await BackofficeLease.TryTakeAsync(db, "lease:t", "a", now, duration, default));
            Assert.False(await BackofficeLease.TryTakeAsync(db, "lease:t", "b", now + TimeSpan.FromMinutes(1), duration, default));
            Assert.True(await BackofficeLease.TryRenewAsync(db, "lease:t", "a", now + TimeSpan.FromMinutes(1), default));
            Assert.False(await BackofficeLease.TryTakeAsync(db, "lease:t", "b", now + TimeSpan.FromMinutes(2), duration, default));
            Assert.True(await BackofficeLease.TryTakeAsync(db, "lease:t", "b", now + TimeSpan.FromMinutes(3), duration, default));
            Assert.False(await BackofficeLease.TryRenewAsync(db, "lease:t", "a", now + TimeSpan.FromMinutes(3), default));
            await BackofficeLease.ReleaseAsync(db, "lease:t", "a", default); // not a's any more: no effect
            Assert.False(await BackofficeLease.TryTakeAsync(db, "lease:t", "c", now + TimeSpan.FromMinutes(3), duration, default));
            await BackofficeLease.ReleaseAsync(db, "lease:t", "b", default);
            Assert.True(await BackofficeLease.TryTakeAsync(db, "lease:t", "c", now + TimeSpan.FromMinutes(3), duration, default));
            return 0;
        });
    }

    [DockerFact]
    public async Task InboxClaim_TwoInstancesNeverGetTheSameItem()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);
        for (var i = 0; i < 30; i++) await host.AddAsync($"msg_{i:D2}", "price.scheduled");

        await using var a = _pg.CreateContext();
        await using var b = _pg.CreateContext();
        var claims = await Task.WhenAll(
            BackofficeInboxProcessor.ClaimDueAsync(a, host.Time.Now, default),
            BackofficeInboxProcessor.ClaimDueAsync(b, host.Time.Now, default));

        Assert.Equal(30, claims[0].Count + claims[1].Count);
        Assert.Empty(claims[0].Intersect(claims[1]));
        Assert.Empty(await BackofficeInboxProcessor.ClaimDueAsync(a, host.Time.Now, default));
        Assert.Equal(30, (await BackofficeInboxProcessor.ClaimDueAsync(a, host.Time.Now + BackofficeInboxProcessor.ClaimDuration, default)).Count);
    }

    [DockerFact]
    public async Task ConcurrentPolls_ReadTheServiceOnce()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        host.Service.Responses.Enqueue(async _ =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[],"next_cursor":"cur_a","has_more":false,"window_clamped":false}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var first = Task.Run(() => host.Polling.PollOnceAsync(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BackofficePollResult second;
        try
        {
            second = await host.Polling.PollOnceAsync(CancellationToken.None);
        }
        finally
        {
            release.SetResult();
        }

        Assert.True(second.Skipped);
        Assert.False((await first).Skipped);
        Assert.Single(host.Service.Requests);
        Assert.Equal("cur_a", await host.CursorAsync());
    }
}
