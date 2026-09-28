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
    public async Task Polling_SkipsWhileAnotherInstanceHoldsTheLock_AndReleasesItsOwn()
    {
        await ClearAsync();
        using var host = new InboxTestHost(_pg.Configure);
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_1", host.Time.Now);
            return 0;
        });

        // Another instance is in the middle of a run.
        await using (var other = new NpgsqlConnection(_pg.ConnectionString))
        {
            await other.OpenAsync();
            await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({BackofficePollingService.AdvisoryLockKey})", other))
                await take.ExecuteNonQueryAsync();

            var skipped = await host.Polling.PollOnceAsync(CancellationToken.None);

            Assert.True(skipped.Skipped);
            Assert.Empty(host.Service.Requests);
            Assert.Equal("cur_1", await host.CursorAsync());

            await using var free = new NpgsqlCommand($"SELECT pg_advisory_unlock({BackofficePollingService.AdvisoryLockKey})", other);
            await free.ExecuteNonQueryAsync();
        }

        // The other instance has finished: this one polls, and frees the lock after.
        host.Service.Enqueue(System.Net.HttpStatusCode.OK,
            """{"data":[],"next_cursor":"cur_2","has_more":false,"window_clamped":false}""");
        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(1, result.Pages);
        Assert.Equal("cur_2", await host.CursorAsync());

        await using var probe = new NpgsqlConnection(_pg.ConnectionString);
        await probe.OpenAsync();
        await using var check = new NpgsqlCommand($"SELECT pg_try_advisory_lock({BackofficePollingService.AdvisoryLockKey})", probe);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
        await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({BackofficePollingService.AdvisoryLockKey})", probe);
        await unlock.ExecuteNonQueryAsync();
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
