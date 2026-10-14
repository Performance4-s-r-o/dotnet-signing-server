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
    private volatile bool _externalCallStarted;

    public OperationDeadline(TimeSpan limit, CancellationToken callerToken)
    {
        Limit = limit;
        _deadline = new CancellationTokenSource(limit);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token, callerToken);
    }

    public TimeSpan Limit { get; }

    /// <summary>Cancelled by the deadline or by the caller giving up (when nobody can ask again).</summary>
    public CancellationToken Token => _linked.Token;

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

    public void Cancel() => _linked.Cancel();

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
