using System.Text.Json;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#69: the thread-pool minimum and the rest of the runtime metrics.</summary>
public class ThreadPoolSizingTests
{
    [Fact]
    public void Default_IsCpusPlusTheAuthorityCallLimit()
    {
        Assert.Equal(28, ThreadPoolSizing.Resolve(null, 4, 24));
        Assert.Equal(25, ThreadPoolSizing.Resolve(null, 0, 24));
    }

    [Fact]
    public void Zero_LeavesTheDotNetDefault() => Assert.Null(ThreadPoolSizing.Resolve(0, 4, 24));

    [Fact]
    public void AValue_IsUsedAsGiven() => Assert.Equal(64, ThreadPoolSizing.Resolve(64, 4, 24));

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void OutOfRange_StopsTheStart(int value) =>
        Assert.Throws<InvalidOperationException>(() => ThreadPoolSizing.Resolve(value, 4, 24));

    [Fact]
    public void Apply_NeverLowersTheMinimum()
    {
        ThreadPool.GetMinThreads(out var before, out _);

        Assert.Equal(before, ThreadPoolSizing.Apply(1));
        Assert.Equal(before, ThreadPoolSizing.Apply(null));
        ThreadPool.GetMinThreads(out var after, out _);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Diagnostics_ShowTheThreadPoolGcAndAuthorityCalls()
    {
        var json = JsonSerializer.Serialize(ConcurrencyDiagnostics.Snapshot(new CapacityPools(new BillingOptions(), 4)));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var name in new[] { "minWorkers", "minIoThreads", "maxWorkers", "completedWorkItems" })
            Assert.True(root.GetProperty("threadPool").TryGetProperty(name, out _), name);
        foreach (var name in new[] { "gcPauseTimePercentage", "gen0Collections", "gen2Collections" })
            Assert.True(root.GetProperty("memory").TryGetProperty(name, out _), name);
        var tsa = root.GetProperty("tsa");
        Assert.True(tsa.GetProperty("limit").GetInt32() >= 1);
        Assert.True(tsa.GetProperty("inFlight").GetInt32() >= 0);
        Assert.True(tsa.TryGetProperty("busyRefusals", out _));
    }
}
