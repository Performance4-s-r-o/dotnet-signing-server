namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Stops calls to the billing API while it is down. <see cref="Threshold"/> consecutive
/// failures (no answer or 5xx) within <see cref="Window"/> open it for <see cref="OpenFor"/>;
/// any other answer closes it. Open means a call is not sent at all and counts as "no answer",
/// which no caller treats as success: a payment or grant is never reported as done because the
/// breaker was open, and a charge stays "unknown" until it is repeated with its own key.
/// </summary>
public sealed class BillingCircuitBreaker
{
    public const int Threshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public BillingCircuitBreaker(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Raised once each time the breaker opens (not on every refused call).</summary>
    public event Action? Opened;

    public bool IsOpen
    {
        get { lock (_gate) return _time.GetUtcNow() < _openUntil; }
    }

    /// <summary>An outage is no answer or a 5xx; a refusal (4xx) says the service is up.</summary>
    public static bool IsFailure(BillingApiResult result) => result.Status is null or >= 500;

    public void Record(BillingApiResult result)
    {
        if (!IsFailure(result))
        {
            lock (_gate)
            {
                _failures.Clear();
                _openUntil = DateTimeOffset.MinValue;
            }
            return;
        }

        var opened = false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > Window) _failures.Dequeue();
            if (_failures.Count >= Threshold)
            {
                opened = now >= _openUntil;
                _openUntil = now + OpenFor;
            }
        }
        if (opened) Opened?.Invoke();
    }
}
