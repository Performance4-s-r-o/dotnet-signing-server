using System.Collections.Concurrent;

namespace DotNetSigningServer.Services;

/// <summary>
/// Raised instead of calling an authority whose breaker is open. Derives from
/// <see cref="HttpRequestException"/> so it travels the same way a refused connection does
/// and ends as <c>TSA_UNREACHABLE</c>, only without the wait.
/// </summary>
public sealed class TsaCircuitOpenException : HttpRequestException
{
    public TsaCircuitOpenException(string host, TimeSpan retryAfter)
        : base($"Circuit breaker for timestamp authority '{host}' is open.")
    {
        Host = host;
        RetryAfter = retryAfter;
    }

    public string Host { get; }
    public TimeSpan RetryAfter { get; }
}

/// <summary>Settings of <see cref="TsaCircuitBreaker"/>; section <c>Tsa:Breaker</c> (#72).</summary>
public sealed class TsaCircuitBreakerOptions
{
    /// <summary>Consecutive failures that open the breaker. <c>0</c> turns it off.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long it stays open before one probe is let through.</summary>
    public int OpenSeconds { get; set; } = 30;

    public const int MaxFailureThreshold = 100;
    public const int MaxOpenSeconds = 3600;

    /// <summary>Throws at startup: a typo must not silently run without protection.</summary>
    public void Validate()
    {
        if (FailureThreshold < 0 || FailureThreshold > MaxFailureThreshold)
        {
            throw new InvalidOperationException(
                $"Tsa:Breaker:FailureThreshold must be within 0..{MaxFailureThreshold} (0 = off), was {FailureThreshold}.");
        }
        if (OpenSeconds < 1 || OpenSeconds > MaxOpenSeconds)
        {
            throw new InvalidOperationException(
                $"Tsa:Breaker:OpenSeconds must be within 1..{MaxOpenSeconds}, was {OpenSeconds}.");
        }
    }
}

/// <summary>
/// Circuit breaker per TSA host (#72): after <c>FailureThreshold</c> consecutive failures the
/// host is refused instantly for <c>OpenSeconds</c>, so a slow or dead authority does not hold
/// a request thread for its whole timeout, twice (primary and backup), on every call. After
/// that one probe call goes through (half-open); its success closes the breaker, its failure
/// opens it again. The fallback to the backup authority is unaffected: an open primary fails
/// instantly and the backup is asked at once.
///
/// State is per process, like the in-memory rate limits. Entries exist only for hosts that
/// failed and are dropped on success, so tenant-chosen URLs cannot grow it without bound.
/// </summary>
public sealed class TsaCircuitBreaker
{
    private const int MaxTrackedHosts = 1024;

    private readonly TsaCircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public TsaCircuitBreaker(TsaCircuitBreakerOptions options, TimeProvider? time = null)
    {
        options.Validate();
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The breaker the shared TSA client uses; replaced at startup from configuration.</summary>
    internal static TsaCircuitBreaker Default { get; set; } = new(new TsaCircuitBreakerOptions());

    private sealed class HostState
    {
        public readonly object Gate = new();
        public int Failures;
        public DateTimeOffset OpenUntil;
        public bool Probing;
    }

    /// <summary>
    /// Whether a call may go out. Throws <see cref="TsaCircuitOpenException"/> when not. The
    /// caller reports the outcome with <see cref="RecordSuccess"/> or <see cref="RecordFailure"/>
    /// (or <see cref="Release"/> when it was cancelled by its own side).
    /// </summary>
    public void Acquire(string host)
    {
        if (_options.FailureThreshold == 0 || !_hosts.TryGetValue(host, out var state)) return;

        lock (state.Gate)
        {
            if (state.Failures < _options.FailureThreshold) return;

            var now = _time.GetUtcNow();
            if (now < state.OpenUntil) throw new TsaCircuitOpenException(host, state.OpenUntil - now);
            if (state.Probing) throw new TsaCircuitOpenException(host, TimeSpan.FromSeconds(1));
            state.Probing = true; // half-open: this call is the probe
        }
    }

    public void RecordSuccess(string host)
    {
        if (_options.FailureThreshold == 0) return;
        _hosts.TryRemove(host, out _);
    }

    public void RecordFailure(string host)
    {
        if (_options.FailureThreshold == 0) return;

        if (_hosts.Count >= MaxTrackedHosts && !_hosts.ContainsKey(host)) Prune();
        var state = _hosts.GetOrAdd(host, _ => new HostState());
        lock (state.Gate)
        {
            state.Failures++;
            state.Probing = false;
            if (state.Failures >= _options.FailureThreshold)
            {
                state.OpenUntil = _time.GetUtcNow().AddSeconds(_options.OpenSeconds);
            }
        }
    }

    /// <summary>The call ended for a reason that says nothing about the host (the caller left).</summary>
    public void Release(string host)
    {
        if (_hosts.TryGetValue(host, out var state))
        {
            lock (state.Gate) state.Probing = false;
        }
    }

    public bool IsOpen(string host) =>
        _hosts.TryGetValue(host, out var s)
        && s.Failures >= _options.FailureThreshold
        && _time.GetUtcNow() < s.OpenUntil;

    private void Prune()
    {
        // Below the threshold nothing is refused yet, so forgetting those hosts costs only
        // their partial count.
        foreach (var (host, state) in _hosts)
        {
            if (state.Failures < _options.FailureThreshold) _hosts.TryRemove(host, out _);
        }
    }
}
