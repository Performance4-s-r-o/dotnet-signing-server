using DotNetSigningServer.Filters;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Middleware;

/// <summary>#69: pools H and A — off by default, bounded wait, every slot given back.</summary>
public class CapacityPoolTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Pool_that_is_off_admits_everyone_and_counts_nothing()
    {
        var pool = new CapacityPool("heavy", 0);
        Assert.False(pool.Enabled);
        Assert.Null(pool.Available);
        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));
        pool.Release();
        Assert.Equal(0, pool.InUse);
    }

    [Fact]
    public async Task Full_pool_waits_then_refuses_and_counts_it()
    {
        var pool = new CapacityPool("heavy", 1);
        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await pool.TryEnterAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None));

        var snapshot = pool.Snapshot();
        Assert.Equal(1, snapshot.InUse);
        Assert.Equal(1, snapshot.Acquired);
        Assert.Equal(1, snapshot.Rejected);
        Assert.Equal(0, snapshot.Waiting);
        Assert.True(snapshot.MaxWaitMs >= 20);

        pool.Release();
        Assert.Equal(1, pool.Available);
    }

    [Fact]
    public async Task Cancelled_wait_takes_no_slot()
    {
        var pool = new CapacityPool("ai", 1);
        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.TryEnterAsync(Bounded, cts.Token));
        pool.Release();
        Assert.Equal(1, pool.Available);
        Assert.Equal(0, pool.Snapshot().Waiting);
    }

    private static (ResourceExecutingContext Context, DefaultHttpContext Http) FilterContext(CapacityPools pools)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddSingleton(pools);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return (new ResourceExecutingContext(action, new List<IFilterMetadata>(), new List<IValueProviderFactory>()), http);
    }

    private static ResourceExecutionDelegate Next(ResourceExecutingContext context, Func<Task> body) => async () =>
    {
        await body();
        return new ResourceExecutedContext(context, new List<IFilterMetadata>());
    };

    [Fact]
    public async Task Filter_holds_a_heavy_slot_for_the_action_and_refuses_with_503_when_full()
    {
        var pools = new CapacityPools(new BillingOptions { HeavyOperationsLimit = 1, GlobalConcurrencyWaitMilliseconds = 0 }, 8);
        var filter = new CapacityPoolAttribute(CapacityPoolKind.Heavy);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (first, _) = FilterContext(pools);
        var running = filter.OnResourceExecutionAsync(first, Next(first, () => release.Task));
        Assert.Equal(1, pools.Heavy.InUse);

        var (second, http) = FilterContext(pools);
        var called = false;
        await filter.OnResourceExecutionAsync(second, Next(second, () => { called = true; return Task.CompletedTask; }));
        Assert.False(called);
        var result = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("10", http.Response.Headers["Retry-After"].ToString());

        release.SetResult();
        await running.WaitAsync(Bounded);
        Assert.Equal(0, pools.Heavy.InUse);
    }

    [Fact]
    public async Task Filter_gives_the_slot_back_when_the_action_throws()
    {
        var pools = new CapacityPools(new BillingOptions { AiOperationsLimit = 1 }, 8);
        var filter = new CapacityPoolAttribute(CapacityPoolKind.Ai);
        var (context, _) = FilterContext(pools);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            filter.OnResourceExecutionAsync(context, Next(context, () => throw new InvalidOperationException("boom"))));
        Assert.Equal(1, pools.Ai.Available);
    }

    [Fact]
    public async Task Filter_with_the_pool_off_only_runs_the_action()
    {
        var pools = new CapacityPools(new BillingOptions(), 8);
        var filter = new CapacityPoolAttribute(CapacityPoolKind.Heavy);
        var (context, _) = FilterContext(pools);
        var called = false;

        await filter.OnResourceExecutionAsync(context, Next(context, () => { called = true; return Task.CompletedTask; }));
        Assert.True(called);
        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData(null, 8086, false)]
    [InlineData(8086, 8085, false)]
    [InlineData(8086, 8086, true)]
    public void Diagnostics_are_served_on_the_internal_listener_only(int? internalPort, int localPort, bool expected)
    {
        var trust = new PortalTrustOptions { InternalPort = internalPort };
        Assert.Equal(expected, ConcurrencyDiagnostics.IsServedOn(trust, localPort));
    }

    [Fact]
    public void Diagnostics_snapshot_lists_the_pools()
    {
        var pools = new CapacityPools(new BillingOptions { HeavyOperationsLimit = 4 }, 8);
        var json = System.Text.Json.JsonSerializer.Serialize(ConcurrencyDiagnostics.Snapshot(pools));
        Assert.Contains("\"heavy\"", json);
        Assert.Contains("\"userQueues\"", json);
        Assert.Contains("\"workingSetBytes\"", json);
    }
}
