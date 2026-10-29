using System.Text.Json;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#69: p95/p99 of operations and of waits for a pool slot, as plain numbers on the diagnostics endpoint.</summary>
public class LatencyMetricsTests
{
    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        var window = new LatencyWindow();
        for (var i = 1; i <= 100; i++) window.Record(i);
        var s = window.Snapshot();
        Assert.Equal(100, s.Count);
        Assert.Equal(50, s.P50Ms);
        Assert.Equal(95, s.P95Ms);
        Assert.Equal(99, s.P99Ms);
        Assert.Equal(100, s.MaxMs);
    }

    [Fact]
    public void An_empty_window_is_zeros()
    {
        Assert.Equal(new LatencySnapshot(0, 0, 0, 0, 0, 0), new LatencyWindow().Snapshot());
    }

    [Fact]
    public void The_window_forgets_old_samples_but_counts_all()
    {
        var window = new LatencyWindow();
        for (var i = 0; i < LatencyWindow.Capacity; i++) window.Record(10_000); // a slow past
        for (var i = 0; i < LatencyWindow.Capacity; i++) window.Record(5);       // a fast present
        var s = window.Snapshot();
        Assert.Equal(LatencyWindow.Capacity * 2, s.Count);
        Assert.Equal(LatencyWindow.Capacity, s.Window);
        Assert.Equal(5, s.P99Ms);
    }

    [Fact]
    public void Operations_are_kept_per_name_with_their_outcome()
    {
        OperationMetrics.Reset();
        OperationMetrics.Record("sign", TimeSpan.FromMilliseconds(100), OperationMetrics.Completed);
        OperationMetrics.Record("sign", TimeSpan.FromMilliseconds(300), OperationMetrics.DeadlineExceeded);
        OperationMetrics.Record("fill-pdf", TimeSpan.FromMilliseconds(50), OperationMetrics.Failed);

        var snapshot = OperationMetrics.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1, snapshot["sign"].Completed);
        Assert.Equal(1, snapshot["sign"].DeadlineExceeded);
        Assert.Equal(300, snapshot["sign"].Latency.MaxMs);
        Assert.Equal(1, snapshot["fill-pdf"].Failed);
    }

    [Fact]
    public void The_number_of_tracked_operations_is_capped()
    {
        OperationMetrics.Reset();
        for (var i = 0; i < OperationMetrics.MaxOperations + 20; i++)
            OperationMetrics.Record($"op-{i}", TimeSpan.FromMilliseconds(1), OperationMetrics.Completed);
        Assert.Equal(OperationMetrics.MaxOperations, OperationMetrics.Snapshot().Count);
    }

    [Fact]
    public async Task A_pool_reports_percentiles_of_the_waits()
    {
        var pool = new CapacityPool("heavy", 1);
        Assert.True(await pool.TryEnterAsync(TimeSpan.Zero, CancellationToken.None));
        for (var i = 0; i < 5; i++) Assert.False(await pool.TryEnterAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));

        var s = pool.Snapshot();
        Assert.True(s.P95WaitMs >= 15, $"p95 {s.P95WaitMs}");
        Assert.True(s.P99WaitMs >= s.P95WaitMs);
        Assert.True(s.MaxWaitMs >= s.P99WaitMs);
    }

    [Fact]
    public void The_diagnostics_snapshot_carries_the_operations_and_nothing_but_numbers_and_fixed_names()
    {
        OperationMetrics.Reset();
        OperationMetrics.Record("sign", TimeSpan.FromMilliseconds(120), OperationMetrics.Completed);
        var json = JsonSerializer.Serialize(ConcurrencyDiagnostics.Snapshot(new CapacityPools(new BillingOptions(), 4)));

        using var doc = JsonDocument.Parse(json);
        var sign = doc.RootElement.GetProperty("operations").GetProperty("sign");
        Assert.Equal(1, sign.GetProperty("Completed").GetInt64());
        Assert.True(sign.GetProperty("Latency").TryGetProperty("P99Ms", out _));
        Assert.True(doc.RootElement.GetProperty("pools")[0].TryGetProperty("P95WaitMs", out _));
        Assert.True(doc.RootElement.GetProperty("memory").TryGetProperty("workingSetBytes", out _));
    }
}
