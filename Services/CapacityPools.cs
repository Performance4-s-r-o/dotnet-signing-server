using System.Diagnostics;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>Which pool an endpoint draws from (#69).</summary>
public enum CapacityPoolKind
{
    /// <summary>H: presign, sign, sign-pfx, seal, visual-sign, timestamp, extend, fill, PDF/A.</summary>
    Heavy,
    /// <summary>A: the AI endpoints.</summary>
    Ai,
    /// <summary>R: rasterisation of a PDF page (Ghostscript + ImageMagick), taken per page.</summary>
    Rasterization,
}

/// <summary>
/// A counting slot pool with a short wait and counters for the diagnostics endpoint (#69).
/// Limit 0 = off: every request passes and nothing is counted as held.
/// </summary>
public sealed class CapacityPool
{
    private readonly SemaphoreSlim? _semaphore;
    private long _waiting;
    private long _acquired;
    private long _rejected;
    private long _waitTicksTotal;
    private long _waitedCount;
    private long _maxWaitTicks;
    private readonly LatencyWindow _waits = new();

    public CapacityPool(string name, int limit)
    {
        Name = name;
        Limit = Math.Max(0, limit);
        if (Limit > 0)
        {
            _semaphore = new SemaphoreSlim(Limit, Limit);
        }
    }

    public string Name { get; }

    public int Limit { get; }

    public bool Enabled => _semaphore != null;

    /// <summary>Free slots, or null when the pool is off.</summary>
    public int? Available => _semaphore?.CurrentCount;

    public int InUse => _semaphore == null ? 0 : Limit - _semaphore.CurrentCount;

    /// <summary>
    /// Takes a slot, waiting at most <paramref name="wait"/>. True = taken (or the pool is off),
    /// false = still full after the wait. Throws when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<bool> TryEnterAsync(TimeSpan wait, CancellationToken ct)
    {
        if (_semaphore == null) return true;
        if (_semaphore.Wait(0))
        {
            Interlocked.Increment(ref _acquired);
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waiting);
        try
        {
            var taken = await _semaphore.WaitAsync(wait, ct);
            if (taken) Interlocked.Increment(ref _acquired);
            else Interlocked.Increment(ref _rejected);
            RecordWait(Stopwatch.GetTimestamp() - started);
            return taken;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Gives back a slot taken by <see cref="TryEnterAsync"/>; a no-op when the pool is off.</summary>
    public void Release() => _semaphore?.Release();

    private void RecordWait(long ticks)
    {
        _waits.RecordTicks(ticks);
        Interlocked.Add(ref _waitTicksTotal, ticks);
        Interlocked.Increment(ref _waitedCount);
        long seen;
        while (ticks > (seen = Interlocked.Read(ref _maxWaitTicks))
               && Interlocked.CompareExchange(ref _maxWaitTicks, ticks, seen) != seen)
        {
        }
    }

    public PoolSnapshot Snapshot()
    {
        var waited = Interlocked.Read(ref _waitedCount);
        var waits = _waits.Snapshot();
        return new PoolSnapshot(
            Name,
            Limit,
            InUse,
            Interlocked.Read(ref _waiting),
            Interlocked.Read(ref _acquired),
            Interlocked.Read(ref _rejected),
            waited,
            waited == 0 ? 0 : TicksToMs(Interlocked.Read(ref _waitTicksTotal)) / waited,
            TicksToMs(Interlocked.Read(ref _maxWaitTicks)),
            waits.P95Ms,
            waits.P99Ms);
    }

    private static double TicksToMs(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);
}

/// <summary>Counts only, no user, tenant or document: safe to show to the operator.</summary>
public sealed record PoolSnapshot(
    string Name,
    int Limit,
    int InUse,
    long Waiting,
    long Acquired,
    long Rejected,
    long Waited,
    double AverageWaitMs,
    double MaxWaitMs,
    double P95WaitMs = 0,
    double P99WaitMs = 0);

/// <summary>
/// The process-wide pools of #69: G (global, taken in <c>UserConcurrencyMiddleware</c>), H (heavy
/// operations), A (AI) and R (rasterisation, taken per page in <see cref="CodeScanRaster"/>).
/// G, H and A are off unless configured; R defaults to max(1, ⌊C/2⌋). The sizes are starting hypotheses from the CPU count, to be
/// tuned by the diagnostics endpoint and a load test, not measured capacity.
/// </summary>
public sealed class CapacityPools
{
    public CapacityPools(IOptions<BillingOptions> billing)
        : this(billing.Value, Environment.ProcessorCount)
    {
    }

    public CapacityPools(BillingOptions billing, int processorCount)
    {
        Global = new CapacityPool("global", BillingOptions.EffectiveGlobalLimit(billing, processorCount));
        Heavy = new CapacityPool("heavy", billing.HeavyOperationsLimit ?? 0);
        Ai = new CapacityPool("ai", billing.AiOperationsLimit ?? 0);
        // R was always on (a fixed semaphore in CodeScanRaster); unset keeps that, 0 switches it off.
        Raster = new CapacityPool("raster", billing.RasterOperationsLimit ?? BillingOptions.SuggestedRasterLimit(processorCount));
        RasterWait = TimeSpan.FromMilliseconds(Math.Max(0, billing.RasterWaitMilliseconds));
        Wait = TimeSpan.FromMilliseconds(Math.Max(0, billing.GlobalConcurrencyWaitMilliseconds));
        HeavyCapacity = Heavy.Enabled ? Heavy.Limit : BillingOptions.SuggestedHeavyLimit(processorCount);
        SelfServiceMaxConcurrency = SelfServiceMax(HeavyCapacity, billing.ConcurrencyDefaultLimit);
    }

    public CapacityPool Global { get; }

    public CapacityPool Heavy { get; }

    public CapacityPool Ai { get; }

    public CapacityPool Raster { get; }

    /// <summary>How long a page waits for a slot in R. Longer than <see cref="Wait"/>: the scan is already paid for.</summary>
    public TimeSpan RasterWait { get; }

    /// <summary>How long a request waits for a slot in G, H or A before the 503.</summary>
    public TimeSpan Wait { get; }

    /// <summary>H in force, or the suggested H while the pool is off; the base of the self-service cap.</summary>
    public int HeavyCapacity { get; }

    /// <summary>
    /// The highest concurrency a user may set for themselves: ⌊H/2⌋, but never below the plan
    /// default (a small machine must still accept it). More only by an operator.
    /// </summary>
    public int SelfServiceMaxConcurrency { get; }

    public CapacityPool Get(CapacityPoolKind kind) => kind switch
    {
        CapacityPoolKind.Heavy => Heavy,
        CapacityPoolKind.Ai => Ai,
        CapacityPoolKind.Rasterization => Raster,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>What a user may save: up to the cap, or keep / lower a higher value an operator stored.</summary>
    public static int AllowedSelfServiceValue(int cap, int? stored) => Math.Max(cap, stored ?? 0);

    public static int SelfServiceMax(int heavyCapacity, int defaultLimit) =>
        Math.Max(Math.Max(1, heavyCapacity / 2), defaultLimit);
}
