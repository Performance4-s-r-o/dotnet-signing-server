using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Tests.Services.Backoffice.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice;

/// <summary>The job lease in <c>BackofficeStates</c> that replaced the session advisory lock (#79), on InMemory.</summary>
public class BackofficeLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(null, 0, true)] // free
    [InlineData("me", 0, true)] // our own
    [InlineData("other", 0, false)] // just renewed by someone else
    [InlineData("other", 119, false)]
    [InlineData("other", 120, true)] // not renewed for its duration: expired
    [InlineData("other", 600, true)]
    public void CanTake_FreeOwnOrExpired(string? holder, int secondsSinceRenewal, bool expected)
    {
        Assert.Equal(expected, BackofficeLease.CanTake(holder, T0, "me", T0.AddSeconds(secondsSinceRenewal), Duration));
    }

    private static Task<BackofficeLease?> AcquireAsync(InboxTestHost host, string name = "job") =>
        BackofficeLease.TryAcquireAsync(host.Services.GetRequiredService<IServiceScopeFactory>(), name, Duration, host.Time,
            NullLogger.Instance, CancellationToken.None);

    private static Task<string?> RowAsync(InboxTestHost host, string name = "job") =>
        host.WithDbAsync(db => BackofficeStateStore.GetAsync(db, BackofficeLease.KeyFor(name)));

    [Fact]
    public async Task SecondHolder_IsRefused_UntilTheFirstReleases()
    {
        using var host = new InboxTestHost();

        var first = await AcquireAsync(host);
        Assert.NotNull(first);
        Assert.Equal(first.Holder, await RowAsync(host));
        Assert.Null(await AcquireAsync(host));
        await using (var other = await AcquireAsync(host, "other-job"))
        {
            Assert.NotNull(other); // leases are per name
        }

        await first.DisposeAsync();
        Assert.Null(await RowAsync(host));
        await using var second = await AcquireAsync(host);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ExpiredLease_IsTakenOver_AndTheOldHolderLearnsItLostIt()
    {
        using var host = new InboxTestHost();
        var first = await AcquireAsync(host);
        Assert.NotNull(first);

        // Renewing keeps it.
        host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await first.RenewAsync(CancellationToken.None));
        host.Time.Advance(TimeSpan.FromMinutes(1.5));
        Assert.Null(await AcquireAsync(host));

        // The holder stalls past the duration: another instance takes over.
        host.Time.Advance(Duration);
        await using var second = await AcquireAsync(host);
        Assert.NotNull(second);

        Assert.False(await first.EnsureHeldAsync(CancellationToken.None));
        Assert.True(first.Lost.IsCancellationRequested);

        // The old holder's release leaves the new lease alone.
        await first.DisposeAsync();
        Assert.Equal(second.Holder, await RowAsync(host));
        Assert.True(await second.EnsureHeldAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Polling_SkipsWhileAnotherInstanceHoldsTheLease()
    {
        using var host = new InboxTestHost();
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeLease.KeyFor(BackofficePollingService.LeaseName), "other-instance", host.Time.Now);
            return 0;
        });

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Empty(host.Service.Requests);

        // A crashed holder blocks polling only until its lease expires.
        host.Time.Advance(BackofficePollingService.LeaseDuration);
        host.Service.Enqueue(HttpStatusCode.OK, """{"data":[],"next_cursor":"cur_1","has_more":false,"window_clamped":false}""");
        Assert.False((await host.Polling.PollOnceAsync(CancellationToken.None)).Skipped);
        Assert.Equal("cur_1", await host.CursorAsync());
        Assert.Null(await RowAsync(host, BackofficePollingService.LeaseName)); // given back after the run
    }

    [Fact]
    public async Task Polling_ThatLostItsLease_DoesNotWriteTheCursor()
    {
        using var host = new InboxTestHost();
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, "cur_0", host.Time.Now);
            return 0;
        });
        host.Service.Responses.Enqueue(async _ =>
        {
            // While this page is in flight the run stalls past its lease and another instance takes over.
            host.Time.Advance(BackofficePollingService.LeaseDuration);
            await host.WithDbAsync(async db =>
            {
                await BackofficeStateStore.SetAsync(db, BackofficeLease.KeyFor(BackofficePollingService.LeaseName), "other-instance", host.Time.Now);
                return 0;
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[],"next_cursor":"cur_1","has_more":true,"window_clamped":false}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var result = await host.Polling.PollOnceAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Single(host.Service.Requests);
        Assert.Equal("cur_0", await host.CursorAsync());
        Assert.Equal("other-instance", await RowAsync(host, BackofficePollingService.LeaseName));
    }
}
