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

/// <summary>A slot taken from a <see cref="CapacityPool"/>; <see cref="Held"/> = false when the pool was off.</summary>
public readonly record struct CapacityLease(bool Admitted, bool Held)
{
    public static readonly CapacityLease Rejected = new(false, false);
}

/// <summary>
/// A counting slot pool with a short wait and counters for the diagnostics endpoint (#69).
/// Limit 0 = off: every request passes and nothing is counted as held.
/// The limit can change while the server runs (#204): a larger pool frees slots at once, a smaller
/// one never stops a running operation; new entries wait until the count falls below the new limit.
/// </summary>
public sealed class CapacityPool
{
    // Always present; with limit 0 its count is 0 and entries pass without it.
    private readonly SemaphoreSlim _semaphore;
    private readonly object _resize = new();
    private int _limit;
    // Slots still to be taken back after a shrink: a release pays the debt instead of freeing a slot.
    private int _debt;
    private long _held;
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
        _limit = Math.Max(0, limit);
        _semaphore = new SemaphoreSlim(_limit, int.MaxValue);
    }

    public string Name { get; }

    public int Limit => Volatile.Read(ref _limit);

    public bool Enabled => Limit > 0;

    /// <summary>Free slots, or null when the pool is off.</summary>
    public int? Available => Enabled ? Math.Max(0, Limit - InUse) : null;

    public int InUse => (int)Interlocked.Read(ref _held);

    /// <summary>
    /// Changes the limit. Growing frees slots for waiting requests; shrinking takes free slots back
    /// now and the rest as running operations finish. 0 switches the pool off and lets the waiting
    /// requests through.
    /// </summary>
    public void SetLimit(int limit)
    {
        limit = Math.Max(0, limit);
        lock (_resize)
        {
            var delta = limit - _limit;
            if (delta == 0) return;
            if (delta > 0)
            {
                var paid = Math.Min(_debt, delta);
                _debt -= paid;
                if (delta - paid > 0) _semaphore.Release(delta - paid);
            }
            else
            {
                _debt += -delta;
                while (_debt > 0 && _semaphore.Wait(0)) _debt--;
            }
            // Written after the slots moved, so a failed release leaves the old limit in force.
            Volatile.Write(ref _limit, limit);
            if (limit == 0)
            {
                // Off: whoever still waits gets a slot that the debt takes back on release.
                var waiting = (int)Interlocked.Read(ref _waiting);
                if (waiting > 0)
                {
                    _semaphore.Release(waiting);
                    _debt += waiting;
                }
            }
        }
    }

    /// <summary>
    /// Takes a slot, waiting at most <paramref name="wait"/>. Admitted = taken (or the pool is off),
    /// not admitted = still full after the wait. Throws when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<CapacityLease> TryAcquireAsync(TimeSpan wait, CancellationToken ct)
    {
        if (!Enabled) return new CapacityLease(true, false);
        if (_semaphore.Wait(0)) return Taken();

        var started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waiting);
        try
        {
            // Switched off between the check above and joining the waiters: no need to wait.
            if (!Enabled) return new CapacityLease(true, false);
            var taken = await _semaphore.WaitAsync(wait, ct);
            RecordWait(Stopwatch.GetTimestamp() - started);
            if (taken) return Taken();
            // Switched off while this request waited: it passes like any other.
            if (!Enabled) return new CapacityLease(true, false);
            Interlocked.Increment(ref _rejected);
            return CapacityLease.Rejected;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private CapacityLease Taken()
    {
        Interlocked.Increment(ref _held);
        Interlocked.Increment(ref _acquired);
        return new CapacityLease(true, true);
    }

    /// <summary>Gives back a slot from <see cref="TryAcquireAsync"/>; a no-op for a lease that holds nothing.</summary>
    public void Release(CapacityLease lease)
    {
        if (!lease.Held) return;
        Interlocked.Decrement(ref _held);
        lock (_resize)
        {
            if (_debt > 0) _debt--;
            else _semaphore.Release();
        }
    }

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

    /// <summary>Slots still to be taken back after a shrink (diagnostics: why InUse can exceed Limit).</summary>
    public int Debt
    {
        get
        {
            lock (_resize) return _debt;
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
            waits.P99Ms,
            Debt);
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
    double P99WaitMs = 0,
    int Debt = 0);

/// <summary>
/// The process-wide pools of #69: G (global, taken in <c>UserConcurrencyMiddleware</c>), H (heavy
/// operations), A (AI) and R (rasterisation, taken per page in <see cref="CodeScanRaster"/>).
/// G, H and A are off unless configured; R defaults to max(1, ⌊C/2⌋). The sizes are starting hypotheses from the CPU count, to be
/// tuned by the diagnostics endpoint and a load test, not measured capacity.
/// </summary>
public sealed class CapacityPools
{
    private readonly int _processorCount;
    private readonly int _defaultUserLimit;
    private long _waitTicks;

    public CapacityPools(IOptions<BillingOptions> billing)
        : this(billing.Value, Environment.ProcessorCount)
    {
    }

    public CapacityPools(BillingOptions billing, int processorCount)
    {
        _processorCount = processorCount;
        _defaultUserLimit = billing.ConcurrencyDefaultLimit;
        Global = new CapacityPool("global", BillingOptions.EffectiveGlobalLimit(billing, processorCount));
        Heavy = new CapacityPool("heavy", billing.HeavyOperationsLimit ?? 0);
        Ai = new CapacityPool("ai", billing.AiOperationsLimit ?? 0);
        // R was always on (a fixed semaphore in CodeScanRaster); unset keeps that, 0 switches it off.
        Raster = new CapacityPool("raster", billing.RasterOperationsLimit ?? BillingOptions.SuggestedRasterLimit(processorCount));
        RasterWait = TimeSpan.FromMilliseconds(Math.Max(0, billing.RasterWaitMilliseconds));
        _waitTicks = TimeSpan.FromMilliseconds(Math.Max(0, billing.GlobalConcurrencyWaitMilliseconds)).Ticks;
    }

    public CapacityPool Global { get; }

    public CapacityPool Heavy { get; }

    public CapacityPool Ai { get; }

    public CapacityPool Raster { get; }

    /// <summary>How long a page waits for a slot in R. Longer than <see cref="Wait"/>: the scan is already paid for.</summary>
    public TimeSpan RasterWait { get; }

    /// <summary>How long a request waits for a slot in G, H or A before the 503.</summary>
    public TimeSpan Wait => TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks));

    /// <summary>H in force, or the suggested H while the pool is off; the base of the self-service cap.</summary>
    public int HeavyCapacity => Heavy.Enabled ? Heavy.Limit : BillingOptions.SuggestedHeavyLimit(_processorCount);

    /// <summary>
    /// The highest concurrency a user may set for themselves: ⌊H/2⌋, but never below the plan
    /// default (a small machine must still accept it). More only by an operator.
    /// </summary>
    public int SelfServiceMaxConcurrency => SelfServiceMax(HeavyCapacity, _defaultUserLimit);

    /// <summary>
    /// Sizes G, H and A and the wait from the runtime settings (#204): env override, then the settings
    /// service, then the default in code (0 = off). Running operations are never dropped.
    /// Returns the pools whose limit changed, for the log.
    /// </summary>
    public IReadOnlyList<string> ApplyRuntimeSettings(Settings.IRuntimeSettings settings)
    {
        var changed = new List<string>();
        void Set(CapacityPool pool, int limit)
        {
            limit = Math.Min(Math.Max(0, limit), BillingOptions.MaxPoolLimit);
            if (pool.Limit == limit) return;
            changed.Add($"{pool.Name} {pool.Limit} → {limit}");
            pool.SetLimit(limit);
        }
        Set(Global, settings.GetInt(Settings.RuntimeSettingKeys.GlobalConcurrencyLimit));
        Set(Heavy, settings.GetInt(Settings.RuntimeSettingKeys.HeavyOperationsLimit));
        Set(Ai, settings.GetInt(Settings.RuntimeSettingKeys.AiOperationsLimit));
        var waitMs = Math.Min(settings.GetInt(Settings.RuntimeSettingKeys.GlobalConcurrencyWaitMs), BillingOptions.MaxGlobalWaitMilliseconds);
        var ticks = TimeSpan.FromMilliseconds(Math.Max(0, waitMs)).Ticks;
        if (Interlocked.Exchange(ref _waitTicks, ticks) != ticks) changed.Add($"wait {waitMs} ms");
        return changed;
    }

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
