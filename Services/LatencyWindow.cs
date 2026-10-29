using System.Collections.Concurrent;
using System.Diagnostics;

namespace DotNetSigningServer.Services;

/// <summary>
/// The last N durations in milliseconds and their percentiles (#69). A ring buffer, not a
/// histogram: the numbers answer "how is it right now", a restart empties them, and nothing here
/// grows. Numbers only.
/// </summary>
public sealed class LatencyWindow
{
    public const int Capacity = 512;

    private readonly double[] _samples = new double[Capacity];
    private readonly object _gate = new();
    private int _next;
    private int _filled;
    private long _total;

    public void Record(TimeSpan elapsed) => Record(elapsed.TotalMilliseconds);

    public void RecordTicks(long stopwatchTicks) => Record(stopwatchTicks * 1000.0 / Stopwatch.Frequency);

    public void Record(double milliseconds)
    {
        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % Capacity;
            if (_filled < Capacity) _filled++;
            _total++;
        }
    }

    public LatencySnapshot Snapshot()
    {
        double[] copy;
        long total;
        lock (_gate)
        {
            copy = new double[_filled];
            Array.Copy(_samples, copy, _filled);
            total = _total;
        }
        if (copy.Length == 0) return new LatencySnapshot(total, 0, 0, 0, 0, 0);
        Array.Sort(copy);
        return new LatencySnapshot(total, copy.Length, Round(Percentile(copy, 0.50)),
            Round(Percentile(copy, 0.95)), Round(Percentile(copy, 0.99)), Round(copy[^1]));
    }

    /// <summary>Nearest rank: the smallest sample with at least p of the samples at or below it.</summary>
    internal static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static double Round(double value) => Math.Round(value, 1);
}

/// <summary>Count since start, size of the window the percentiles come from, then the percentiles.</summary>
public sealed record LatencySnapshot(long Count, int Window, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

/// <summary>
/// How long paid operations take from the start of the work until it ends or is given up on,
/// per operation, and how they ended (#69). Keys are the fixed operation names from the code
/// (<c>sign</c>, <c>fill-pdf</c>, …), never a user, tenant, path or document.
/// </summary>
public static class OperationMetrics
{
    internal const int MaxOperations = 64;

    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string DeadlineExceeded = "deadline";

    private sealed class Entry
    {
        public readonly LatencyWindow Latency = new();
        public long Completed;
        public long Failed;
        public long DeadlineExceeded;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    public static void Record(string operation, TimeSpan elapsed, string outcome)
    {
        if (string.IsNullOrEmpty(operation)) return;
        if (!Entries.TryGetValue(operation, out var entry))
        {
            // A closed set in practice; the cap is a guard against a caller that ever varies it.
            if (Entries.Count >= MaxOperations) return;
            entry = Entries.GetOrAdd(operation, _ => new Entry());
        }
        entry.Latency.Record(elapsed);
        switch (outcome)
        {
            case Completed: Interlocked.Increment(ref entry.Completed); break;
            case DeadlineExceeded: Interlocked.Increment(ref entry.DeadlineExceeded); break;
            default: Interlocked.Increment(ref entry.Failed); break;
        }
    }

    public static IReadOnlyDictionary<string, OperationSnapshot> Snapshot() =>
        Entries.ToDictionary(
            e => e.Key,
            e => new OperationSnapshot(
                e.Value.Latency.Snapshot(),
                Interlocked.Read(ref e.Value.Completed),
                Interlocked.Read(ref e.Value.Failed),
                Interlocked.Read(ref e.Value.DeadlineExceeded)));

    /// <summary>Tests only.</summary>
    internal static void Reset() => Entries.Clear();
}

public sealed record OperationSnapshot(LatencySnapshot Latency, long Completed, long Failed, long DeadlineExceeded);
