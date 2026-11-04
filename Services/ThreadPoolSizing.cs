namespace DotNetSigningServer.Services;

/// <summary>
/// Minimum worker threads of the thread pool (#69). Below the minimum the pool adds a thread at
/// once, above it only slowly (about one or two a second), so a burst of authority calls, each
/// holding a blocked thread (#72), would otherwise starve every other request for seconds.
/// The default is the CPUs plus the authority-call limit: the blocked calls can take their
/// share and the CPUs still find free threads. It only ever raises the .NET default.
/// </summary>
public static class ThreadPoolSizing
{
    public const string ConfigKey = "THREADPOOL_MIN_WORKER_THREADS";
    public const int MaxWorkers = 10_000;

    public static int Suggested(int processorCount, int tsaCallLimit) =>
        Math.Max(1, processorCount) + Math.Max(0, tsaCallLimit);

    /// <summary>
    /// The minimum to apply: empty = <see cref="Suggested"/>, 0 = leave the .NET default (null),
    /// 1–<see cref="MaxWorkers"/> as given; anything else stops the start.
    /// </summary>
    public static int? Resolve(int? configured, int processorCount, int tsaCallLimit)
    {
        if (configured is null) return Suggested(processorCount, tsaCallLimit);
        if (configured == 0) return null;
        if (configured is < 0 or > MaxWorkers)
        {
            throw new InvalidOperationException($"{ConfigKey} must be 0 (the .NET default) or 1-{MaxWorkers}.");
        }
        return configured;
    }

    /// <summary>Raises the minimum to <paramref name="workers"/> (never lowers it); returns the minimum in force.</summary>
    public static int Apply(int? workers)
    {
        ThreadPool.GetMinThreads(out var current, out var io);
        if (workers is { } wanted && wanted > current && ThreadPool.SetMinThreads(wanted, io))
        {
            return wanted;
        }
        return current;
    }
}
