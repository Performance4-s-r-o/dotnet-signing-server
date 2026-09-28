namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// What one attempt to send an outbox item produced: an HTTP answer, or no answer at all
/// (network error, timeout). Classified by <see cref="RetryPolicy"/>.
/// </summary>
public sealed record OutboxAttemptResult
{
    /// <summary>HTTP status; null when no response arrived.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Machine <c>code</c> of an RFC 9457 problem body, when there was one.</summary>
    public string? ProblemCode { get; init; }

    /// <summary>Id of the created resource from a successful response.</summary>
    public string? RemoteId { get; init; }

    /// <summary>The service's <c>Retry-After</c>, if it sent one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Short description for <c>LastError</c> and logs; never contains the payload.</summary>
    public string? Error { get; init; }

    public bool IsTimeout { get; init; }

    /// <summary>The attempt never reached the network (no handler, unreadable payload, handler bug).</summary>
    public bool IsLocalFailure { get; init; }

    /// <summary>A local failure that no retry can fix (payload cannot be decrypted): the item goes Dead.</summary>
    public bool IsFatal { get; init; }

    public static OutboxAttemptResult Response(
        int statusCode, string? problemCode = null, string? remoteId = null, TimeSpan? retryAfter = null) => new()
        {
            StatusCode = statusCode,
            ProblemCode = problemCode,
            RemoteId = remoteId,
            RetryAfter = retryAfter,
            Error = statusCode is >= 200 and < 300
            ? null
            : problemCode is null ? $"HTTP {statusCode}" : $"HTTP {statusCode} {problemCode}",
        };

    public static OutboxAttemptResult NetworkError(string message) => new() { Error = "Network: " + message };

    public static OutboxAttemptResult Timeout() => new() { Error = "Timeout", IsTimeout = true };

    /// <summary>The attempt could not even be made (no handler, handler bug); retried like a network error.</summary>
    public static OutboxAttemptResult LocalFailure(string message) => new() { Error = message, IsLocalFailure = true };

    /// <summary>A local failure that retrying cannot fix.</summary>
    public static OutboxAttemptResult Fatal(string message) => new() { Error = message, IsLocalFailure = true, IsFatal = true };
}
