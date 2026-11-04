using System.Diagnostics;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// What the concurrency limits are doing (#69): pool occupancy and waits, 429/503 counts,
/// process memory and thread pool. Counts only — no user, tenant, path or document. Served on
/// the internal listener only, so it is not public and needs no token of its own.
/// </summary>
public static class ConcurrencyDiagnostics
{
    public const string Path = "/internal/concurrency";

    /// <summary>Only on the internal listener; without one the endpoint does not exist.</summary>
    public static bool IsServedOn(InternalListenerOptions listener, int localPort) => listener.IsInternal(localPort);

    public static object Snapshot(CapacityPools pools)
    {
        var cpus = Environment.ProcessorCount;
        ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        var gc = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();

        return new
        {
            processorCount = cpus,
            userQueues = UserConcurrencyMiddleware.QueuesSnapshot(),
            pools = new[]
            {
                pools.Global.Snapshot(),
                pools.Heavy.Snapshot(),
                pools.Ai.Snapshot(),
                pools.Raster.Snapshot(),
            },
            operations = OperationMetrics.Snapshot(),
            suggested = new
            {
                heavy = BillingOptions.SuggestedHeavyLimit(cpus),
                raster = BillingOptions.SuggestedRasterLimit(cpus),
                ai = BillingOptions.SuggestedAiLimit,
                global = BillingOptions.SuggestedGlobalLimit(cpus),
                selfServiceMaxConcurrency = pools.SelfServiceMaxConcurrency,
            },
            memory = new
            {
                workingSetBytes = process.WorkingSet64,
                gcHeapBytes = GC.GetTotalMemory(false),
                gcCommittedBytes = gc.TotalCommittedBytes,
                gcTotalAvailableBytes = gc.TotalAvailableMemoryBytes,
                gcPauseTimePercentage = gc.PauseTimePercentage,
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2),
            },
            threadPool = new
            {
                threads = ThreadPool.ThreadCount,
                busyWorkers = maxWorkers - freeWorkers,
                pendingWorkItems = ThreadPool.PendingWorkItemCount,
                completedWorkItems = ThreadPool.CompletedWorkItemCount,
                minWorkers,
                minIoThreads = minIo,
                maxWorkers,
            },
            tsa = new
            {
                limit = PinnedIpTsaClient.CallLimit,
                inFlight = PinnedIpTsaClient.CallsInFlight,
                busyRefusals = PinnedIpTsaClient.BusyRefusals,
            },
        };
    }
}
