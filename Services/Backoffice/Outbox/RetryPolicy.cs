using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>How an attempt ends for the item.</summary>
public enum OutboxOutcome
{
    /// <summary>Accepted by the service.</summary>
    Sent,

    /// <summary>Temporary failure; try again later.</summary>
    Retry,

    /// <summary>Unauthorised (401/403): kept until an admin requeues it after fixing the key.</summary>
    Blocked,

    /// <summary>Refused for good (other 4xx) or out of retries — an error worth an alert.</summary>
    Dead,

    /// <summary>Refused because the recipient is suppressed (422 <c>suppressed_recipient</c>): dead, but expected.</summary>
    Suppressed,
}

/// <summary>
/// Classification of attempt results and the retry schedule. Pure — no clock, no I/O.
///
/// Schedule after the 1st, 2nd, … failed attempt: 5 s, 30 s, 2 min, 10 min, 30 min, 1 h,
/// then every 6 h. An item is given up (Dead) once the schedule has run for 72 h, i.e.
/// when the next attempt would fall more than 72 h after the first one of its round.
/// Counting by attempts rather than by <c>CreatedAt</c> lets a requeued item (attempts
/// reset to 0) start a fresh round.
/// </summary>
public static class RetryPolicy
{
    public static readonly IReadOnlyList<TimeSpan> Schedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    ];

    public static readonly TimeSpan SteadyDelay = TimeSpan.FromHours(6);

    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(72);

    public const string SuppressedRecipient = "suppressed_recipient";

    /// <summary>The service is still processing the first request with this key.</summary>
    public const string IdempotencyInProgress = "idempotency_in_progress";

    public const int MaxErrorLength = 512;
    public const int MaxRemoteIdLength = 64;

    public static OutboxOutcome Classify(OutboxAttemptResult result)
    {
        if (result.IsFatal) return OutboxOutcome.Dead;
        if (result.IsSuppressed) return OutboxOutcome.Suppressed;
        if (result.StatusCode is not { } status) return OutboxOutcome.Retry;

        if (status is >= 200 and < 300) return OutboxOutcome.Sent;
        if (status is 408 or 429 or >= 500) return OutboxOutcome.Retry;
        if (status is 401 or 403) return OutboxOutcome.Blocked;

        if (status == 409 && result.ProblemCode?.StartsWith("idempotency_", StringComparison.Ordinal) == true)
        {
            // The key was already used for this very request. "In progress" means the first
            // request has not finished yet (it may still fail and roll back) — ask again.
            // Anything else means it is done: success, the effect exists exactly once.
            return result.ProblemCode == IdempotencyInProgress ? OutboxOutcome.Retry : OutboxOutcome.Sent;
        }

        if (status == 422 && result.ProblemCode == SuppressedRecipient) return OutboxOutcome.Suppressed;

        return OutboxOutcome.Dead;
    }

    /// <summary>Delay after the <paramref name="failedAttempts"/>-th failed attempt (1-based).</summary>
    public static TimeSpan DelayAfter(int failedAttempts)
    {
        if (failedAttempts < 1) return TimeSpan.Zero;
        return failedAttempts <= Schedule.Count ? Schedule[failedAttempts - 1] : SteadyDelay;
    }

    /// <summary>Time from the first attempt of a round to the attempt after <paramref name="failedAttempts"/> failures.</summary>
    public static TimeSpan ElapsedBefore(int failedAttempts)
    {
        var total = TimeSpan.Zero;
        for (var n = 1; n <= failedAttempts; n++) total += DelayAfter(n);
        return total;
    }

    /// <summary>Network errors, timeouts and 5xx: what makes the service count as down.</summary>
    public static bool IsServiceFailure(OutboxAttemptResult result) =>
        !result.IsLocalFailure && result.StatusCode is null or >= 500;

    /// <summary>
    /// Records an attempt on the item: increments <c>Attempts</c>, releases the claim and sets
    /// the status, next attempt, error and — on success — the remote id, clearing the payload.
    /// </summary>
    public static OutboxOutcome Apply(BackofficeOutboxItem item, OutboxAttemptResult result, DateTimeOffset now)
    {
        var outcome = Classify(result);
        item.Attempts++;
        item.LockedUntil = null;

        switch (outcome)
        {
            case OutboxOutcome.Sent:
                item.Status = BackofficeOutboxStatus.Sent;
                item.SentAt = now;
                item.RemoteId = Truncate(result.RemoteId, MaxRemoteIdLength);
                item.PayloadProtected = null;
                item.LastError = null;
                break;

            case OutboxOutcome.Retry:
                var elapsed = ElapsedBefore(item.Attempts);
                if (elapsed > GiveUpAfter)
                {
                    item.Status = BackofficeOutboxStatus.Dead;
                    item.LastError = Truncate($"Gave up after {item.Attempts} attempts: {result.Error}", MaxErrorLength);
                    outcome = OutboxOutcome.Dead;
                    break;
                }
                var delay = DelayAfter(item.Attempts);
                if (result.RetryAfter is { } retryAfter && retryAfter > delay)
                {
                    delay = retryAfter < SteadyDelay ? retryAfter : SteadyDelay;
                }
                item.NextAttemptAt = now + delay;
                item.LastError = Truncate(result.Error, MaxErrorLength);
                break;

            case OutboxOutcome.Blocked:
                item.Status = BackofficeOutboxStatus.Blocked;
                item.LastError = Truncate(result.Error, MaxErrorLength);
                break;

            case OutboxOutcome.Dead:
                item.Status = BackofficeOutboxStatus.Dead;
                item.LastError = Truncate(result.Error, MaxErrorLength);
                break;

            case OutboxOutcome.Suppressed:
                // Never going to be sent by anyone: the content is not kept.
                item.Status = BackofficeOutboxStatus.Dead;
                item.RemoteId = Truncate(result.RemoteId, MaxRemoteIdLength);
                item.PayloadProtected = null;
                item.LastError = Truncate(result.Error, MaxErrorLength);
                break;
        }

        return outcome;
    }

    internal static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
