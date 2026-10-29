using DotNetSigningServer.Exceptions;
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
    public async Task Filter_client_gone_while_waiting_ends_empty_and_takes_no_slot()
    {
        var pools = new CapacityPools(new BillingOptions { HeavyOperationsLimit = 1, GlobalConcurrencyWaitMilliseconds = 10_000 }, 8);
        var filter = new CapacityPoolAttribute(CapacityPoolKind.Heavy);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (first, _) = FilterContext(pools);
        var running = filter.OnResourceExecutionAsync(first, Next(first, () => release.Task));

        var (waiting, http) = FilterContext(pools);
        using var aborted = new CancellationTokenSource();
        http.RequestAborted = aborted.Token;
        var called = false;
        var waitingTask = filter.OnResourceExecutionAsync(waiting, Next(waiting, () => { called = true; return Task.CompletedTask; }));
        Assert.False(waitingTask.IsCompleted);
        aborted.Cancel();
        await waitingTask.WaitAsync(Bounded);

        Assert.False(called);
        Assert.IsType<EmptyResult>(waiting.Result);
        Assert.Equal(1, pools.Heavy.InUse);
        release.SetResult();
        await running.WaitAsync(Bounded);
        Assert.Equal(1, pools.Heavy.Available);
        Assert.Equal(0, pools.Heavy.Snapshot().Waiting);
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

    // ── Pool R (rasterisation) ─────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(8, 4)]
    [InlineData(24, 12)]
    public void Raster_pool_defaults_to_half_the_cores_but_at_least_one(int cores, int expected)
    {
        var pools = new CapacityPools(new BillingOptions(), cores);
        Assert.True(pools.Raster.Enabled);
        Assert.Equal(expected, pools.Raster.Limit);
        Assert.Same(pools.Raster, pools.Get(CapacityPoolKind.Rasterization));
    }

    [Fact]
    public void Raster_pool_is_overridden_by_configuration_and_zero_switches_it_off()
    {
        Assert.Equal(2, new CapacityPools(new BillingOptions { RasterOperationsLimit = 2 }, 24).Raster.Limit);
        Assert.False(new CapacityPools(new BillingOptions { RasterOperationsLimit = 0 }, 24).Raster.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), new CapacityPools(new BillingOptions { RasterWaitMilliseconds = 5000 }, 8).RasterWait);
    }

    [Fact]
    public async Task Raster_slot_is_given_back_after_a_result_an_exception_and_a_cancellation()
    {
        var pool = new CapacityPool("raster", 1);

        Assert.Equal(7, await CodeScanRaster.WithSlotAsync(pool, Bounded, () => 7, CancellationToken.None));
        Assert.Equal(1, pool.Available);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodeScanRaster.WithSlotAsync<int>(pool, Bounded, () => throw new InvalidOperationException("boom"), CancellationToken.None));
        Assert.Equal(1, pool.Available);

        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodeScanRaster.WithSlotAsync(pool, Bounded, () => 1, cts.Token));
        pool.Release();
        Assert.Equal(1, pool.Available);
        Assert.Equal(0, pool.Snapshot().Waiting);
    }

    [Fact]
    public async Task Raster_page_waits_for_the_slot_then_gets_capacity_exceeded()
    {
        var pool = new CapacityPool("raster", 1);
        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));

        var started = false;
        var ex = await Assert.ThrowsAsync<CapacityExceededException>(() =>
            CodeScanRaster.WithSlotAsync(pool, TimeSpan.FromMilliseconds(40), () => { started = true; return 1; }, CancellationToken.None));

        Assert.False(started);
        Assert.Equal("raster", ex.Pool);
        Assert.Equal(1, pool.Snapshot().Rejected);
        Assert.Equal(1, pool.InUse);
    }

    [Fact]
    public async Task Raster_pages_run_in_order_of_arrival_and_never_more_than_the_limit_at_once()
    {
        var pool = new CapacityPool("raster", 2);
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            CodeScanRaster.WithSlotAsync(pool, Bounded, () =>
            {
                var now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                gate.Task.Wait(Bounded);
                Interlocked.Decrement(ref running);
                return 0;
            }, CancellationToken.None))).ToArray();

        SpinWait.SpinUntil(() => pool.InUse == 2 && pool.Snapshot().Waiting == 4, Bounded);
        Assert.Equal(2, pool.InUse);
        gate.SetResult();
        await Task.WhenAll(tasks).WaitAsync(Bounded);

        Assert.Equal(2, peak);
        Assert.Equal(0, pool.InUse);
        Assert.Equal(6, pool.Snapshot().Acquired);
    }

    [Fact]
    public void Diagnostics_snapshot_lists_the_raster_pool()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ConcurrencyDiagnostics.Snapshot(new CapacityPools(new BillingOptions(), 8)));
        Assert.Contains("\"raster\"", json);
    }
}
