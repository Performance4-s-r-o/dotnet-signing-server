namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// When a write to the billing API is repeated, and with which <c>Idempotency-Key</c>. Pure.
///
/// The service stores the first answer of a key for 24 hours and derives its Stripe
/// idempotency keys from it, so:
/// <list type="bullet">
/// <item>no answer, a 5xx, <c>429</c> or <c>409 idempotency_in_progress</c> — the request may
/// have reached Stripe: repeat it with the <b>same</b> key (never a second payment);</item>
/// <item>any other 4xx — the request was refused and nothing happened: a corrected request
/// needs a <b>new</b> key (the same key would replay the refusal).</item>
/// </list>
/// </summary>
public static class BillingRetryPolicy
{
    public const string IdempotencyInProgress = "idempotency_in_progress";

    /// <summary>Attempts within one call (the first one included).</summary>
    public const int MaxAttempts = 2;

    /// <summary>Whether the same request may be sent again with the same key.</summary>
    public static bool RetryWithSameKey(BillingApiResult result) =>
        result.Status switch
        {
            null => true,
            >= 500 => true,
            429 => true,
            409 => result.ProblemCode == IdempotencyInProgress,
            _ => false,
        };

    /// <summary>Wait before attempt <paramref name="attempt"/> (2 = the first repeat).</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(400 * Math.Max(1, attempt - 1));
}
