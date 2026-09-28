namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// In-memory view of whether the service is down, fed by the dispatcher's attempts.
/// <see cref="Threshold"/> consecutive network/5xx failures within <see cref="Window"/> mark
/// the service down for <see cref="OpenFor"/>; any other answer resets it. Used by the
/// break-glass e-mail path to skip waiting for a service that is known to be down.
/// </summary>
public sealed class OutboxCircuitBreaker
{
    public const int Threshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OpenFor = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _downUntil = DateTimeOffset.MinValue;

    public OutboxCircuitBreaker(TimeProvider time)
    {
        _time = time;
    }

    public bool IsServiceDown
    {
        get { lock (_gate) return _time.GetUtcNow() < _downUntil; }
    }

    public void Record(OutboxAttemptResult result)
    {
        // A local failure (no handler, …) says nothing about the service.
        if (result.IsLocalFailure) return;
        if (RetryPolicy.IsServiceFailure(result)) RecordFailure();
        else RecordSuccess();
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > Window) _failures.Dequeue();
            if (_failures.Count >= Threshold) _downUntil = now + OpenFor;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures.Clear();
            _downUntil = DateTimeOffset.MinValue;
        }
    }
}
