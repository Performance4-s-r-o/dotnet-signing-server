using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Settings;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#204: pools G, H and A change size while the server runs; nothing running is dropped.</summary>
public class CapacityPoolResizeTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Growing_lets_a_waiting_request_through()
    {
        var pool = new CapacityPool("heavy", 1);
        var first = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var waiting = pool.TryAcquireAsync(Bounded, CancellationToken.None);
        await WaitUntil(() => pool.Snapshot().Waiting == 1);

        pool.SetLimit(2);
        var second = await waiting;

        Assert.True(second.Held);
        Assert.Equal(2, pool.InUse);
        pool.Release(first);
        pool.Release(second);
        Assert.Equal(2, pool.Available);
    }

    [Fact]
    public async Task Shrinking_keeps_running_operations_and_new_ones_wait_below_the_new_limit()
    {
        var pool = new CapacityPool("heavy", 3);
        var a = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var b = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var c = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);

        pool.SetLimit(1);
        Assert.Equal(3, pool.InUse);
        Assert.False((await pool.TryAcquireAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Admitted);

        pool.Release(a);
        pool.Release(b);
        // Two still over the old limit were paid back as debt; one still runs = full.
        Assert.False((await pool.TryAcquireAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Admitted);
        pool.Release(c);
        var d = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.True(d.Held);
        Assert.False((await pool.TryAcquireAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Admitted);
        pool.Release(d);
        Assert.Equal(1, pool.Available);
    }

    [Fact]
    public async Task Switching_off_admits_waiting_requests_and_switching_on_again_counts_from_zero()
    {
        var pool = new CapacityPool("global", 1);
        var running = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var waiting = pool.TryAcquireAsync(Bounded, CancellationToken.None);
        await WaitUntil(() => pool.Snapshot().Waiting == 1);

        pool.SetLimit(0);
        var admitted = await waiting;
        Assert.True(admitted.Admitted);
        var bypass = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.False(bypass.Held);

        pool.Release(running);
        pool.Release(admitted);
        pool.Release(bypass);
        Assert.Equal(0, pool.InUse);

        pool.SetLimit(2);
        Assert.Equal(2, pool.Available);
        var x = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var y = await pool.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.True(x.Held && y.Held);
        Assert.False((await pool.TryAcquireAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Admitted);
    }

    [Fact]
    public void Runtime_settings_size_the_pools_and_env_still_wins()
    {
        var pools = new CapacityPools(new BillingOptions(), 8);
        var settings = TestRuntimeSettings.With(("AI_OPERATIONS_LIMIT", "0"));
        settings.Apply(RuntimeSettingsSnapshot.Parse("""
            {"version":1,"items":[
              {"key":"global_concurrency_limit","type":"int","value":40,"default":0,"fail_mode":"last_known"},
              {"key":"heavy_operations_limit","type":"int","value":6,"default":0,"fail_mode":"last_known"},
              {"key":"ai_operations_limit","type":"int","value":3,"default":0,"fail_mode":"last_known"},
              {"key":"global_concurrency_wait_ms","type":"int","value":500,"default":2000,"fail_mode":"last_known"}]}
            """), DateTimeOffset.UtcNow);

        var changed = pools.ApplyRuntimeSettings(settings);

        Assert.Equal(40, pools.Global.Limit);
        Assert.Equal(6, pools.Heavy.Limit);
        Assert.Equal(0, pools.Ai.Limit); // env 0 = off, whatever the service says
        Assert.Equal(TimeSpan.FromMilliseconds(500), pools.Wait);
        Assert.Equal(3, pools.SelfServiceMaxConcurrency);
        Assert.Contains("heavy 0 → 6", changed);
        Assert.Empty(pools.ApplyRuntimeSettings(settings));
    }

    [Fact]
    public void Without_a_service_value_the_pools_stay_off_as_before()
    {
        var pools = new CapacityPools(new BillingOptions(), 8);
        pools.ApplyRuntimeSettings(TestRuntimeSettings.Defaults);
        Assert.False(pools.Global.Enabled);
        Assert.False(pools.Heavy.Enabled);
        Assert.False(pools.Ai.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), pools.Wait);
    }

    [Fact]
    public void Service_values_over_the_bound_are_capped()
    {
        var pools = new CapacityPools(new BillingOptions(), 8);
        var settings = TestRuntimeSettings.Defaults;
        settings.Apply(RuntimeSettingsSnapshot.Parse("""
            {"version":1,"items":[
              {"key":"heavy_operations_limit","type":"int","value":999999,"default":0,"fail_mode":"last_known"},
              {"key":"global_concurrency_wait_ms","type":"int","value":999999,"default":2000,"fail_mode":"last_known"}]}
            """), DateTimeOffset.UtcNow);
        pools.ApplyRuntimeSettings(settings);
        Assert.Equal(BillingOptions.MaxPoolLimit, pools.Heavy.Limit);
        Assert.Equal(TimeSpan.FromMilliseconds(BillingOptions.MaxGlobalWaitMilliseconds), pools.Wait);
    }

    [Fact]
    public void Refresher_logs_nothing_when_nothing_changed()
    {
        var pools = new CapacityPools(new BillingOptions(), 4);
        var refresher = new CapacityPoolsRefresher(pools, TestRuntimeSettings.With(("HEAVY_OPERATIONS_LIMIT", "2")), TimeProvider.System,
            NullLogger<CapacityPoolsRefresher>.Instance);
        refresher.Refresh();
        Assert.Equal(2, pools.Heavy.Limit);
        refresher.Refresh();
        Assert.Equal(2, pools.Heavy.Limit);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }
}
