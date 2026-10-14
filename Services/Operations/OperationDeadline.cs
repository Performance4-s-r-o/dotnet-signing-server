namespace DotNetSigningServer.Services.Operations;

/// <summary>
/// The deadline of one paid operation (#72), carried by the call so that code deep inside
/// iText callbacks (the TSA client, the revocation clients) can see it without every
/// signature growing a token parameter.
///
/// Two things are tracked: when the work has to be over, and whether it already reached
/// something outside — a timestamp authority or a model. The second decides what an
/// expired operation is: <c>failed</c> when nothing external was touched, <c>failed_uncertain</c>
/// when the TSA or the model may have done its part.
///
/// iText itself cannot be interrupted, so the deadline is cooperative where the work awaits
/// (outbound calls, loops that check <see cref="ThrowIfCancelled"/>) and a hard answer
/// deadline everywhere else: the caller gets its response on time and the orphaned work
/// finds a cancelled token at its next outbound call.
/// </summary>
public sealed class OperationDeadline : IDisposable
{
    private static readonly AsyncLocal<OperationDeadline?> Ambient = new();

    /// <summary>How long a cancelled attempt is given to unwind before its state is settled.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _token;
    private volatile bool _externalCallStarted;

    public OperationDeadline(TimeSpan limit, CancellationToken callerToken)
    {
        Limit = limit;
        _deadline = new CancellationTokenSource(limit);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token, callerToken);
        // Held as a value: abandoned work keeps reading it after the request is gone and the
        // sources are disposed, and a token struct stays valid where its source's property throws.
        _token = _linked.Token;
    }

    public TimeSpan Limit { get; }

    /// <summary>Cancelled by the deadline or by the caller giving up (when nobody can ask again).</summary>
    public CancellationToken Token => _token;

    public bool Expired => _deadline.IsCancellationRequested;

    /// <summary>A TSA or model call was started: it may have happened whatever came back.</summary>
    public bool ExternalCallStarted => _externalCallStarted;

    /// <summary>The ambient token of the running operation; none outside one.</summary>
    public static CancellationToken CurrentToken => Ambient.Value?.Token ?? CancellationToken.None;

    /// <summary>Called by code about to talk to a TSA or a model.</summary>
    public static void MarkExternalCall()
    {
        if (Ambient.Value is { } current) current._externalCallStarted = true;
    }

    /// <summary>A cheap checkpoint for loops over pages, rows or signatures.</summary>
    public static void ThrowIfCancelled() => CurrentToken.ThrowIfCancellationRequested();

    /// <summary>Starts <paramref name="work"/> on the pool with this deadline as the ambient one.</summary>
    public Task<T> Run<T>(Func<CancellationToken, Task<T>> work) =>
        Task.Run(() =>
        {
            Ambient.Value = this;
            return work(Token);
        });

    public void Cancel()
    {
        try { _linked.Cancel(); }
        catch (ObjectDisposedException) { /* already over */ }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
    }
}

/// <summary>The operation did not finish within its deadline; the work was cancelled.</summary>
public sealed class OperationDeadlineExceededException : Exception
{
    public OperationDeadlineExceededException(TimeSpan limit, bool externalCallStarted)
        : base($"The operation did not finish within {limit.TotalSeconds:0} s.")
    {
        ExternalCallStarted = externalCallStarted;
    }

    /// <summary>The TSA or model may have done its part: the state is <c>failed_uncertain</c>.</summary>
    public bool ExternalCallStarted { get; }
}

/// <summary>
/// Work that outlived its deadline and is still running (#72). iText cannot be interrupted,
/// so after a 504 the task keeps its CPU and memory until it ends; the caller's concurrency
/// slot is already free, so retrying on 504 would otherwise stack up unbounded running jobs.
/// New paid operations are refused while a user (or the process) has too many of these.
/// </summary>
public static class AbandonedWork
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, int> PerUser = new();
    private static int _total;

    public static int Total { get { lock (Gate) return _total; } }

    public static int For(Guid userId)
    {
        lock (Gate) return PerUser.TryGetValue(userId, out var n) ? n : 0;
    }

    /// <summary>Whether a new operation of this user must be refused, and whether the process limit says so.</summary>
    public static bool IsOverLimit(Guid userId, int perUser, int total, out bool global)
    {
        lock (Gate)
        {
            global = _total >= total;
            return global || (PerUser.TryGetValue(userId, out var n) && n >= perUser);
        }
    }

    /// <summary>Counts <paramref name="running"/> as abandoned until it ends, however it ends.</summary>
    public static void Track(Guid userId, Task running, Action? whenOver = null)
    {
        lock (Gate)
        {
            _total++;
            PerUser[userId] = PerUser.GetValueOrDefault(userId) + 1;
        }
        _ = running.ContinueWith(t =>
        {
            _ = t.Exception; // observed
            lock (Gate)
            {
                _total--;
                var left = PerUser.GetValueOrDefault(userId) - 1;
                if (left <= 0) PerUser.Remove(userId); else PerUser[userId] = left;
            }
            whenOver?.Invoke();
        }, TaskScheduler.Default);
    }
}
