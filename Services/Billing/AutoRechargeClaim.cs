namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The one auto-recharge charge of a user that may be running, shared by every replica: a
/// row in <c>WebhookEvents</c> (unique <c>EventId</c>) holding the charge's
/// <c>Idempotency-Key</c>. While it exists no second charge with another key starts; a charge
/// that got no answer is repeated with the same key, so the service answers with the same
/// payment instead of charging again. The key also travels in the payment's metadata
/// (<see cref="MetadataKey"/>), so only the event of this very charge releases the claim.
/// </summary>
/// <param name="Key">Idempotency-Key of the charge.</param>
/// <param name="FirstAttemptAt">When the key was first sent (the service keeps its answer 24 hours from then).</param>
/// <param name="ClaimedAt">When the current attempt started.</param>
/// <param name="RetryReady">The last attempt got no final answer and may be repeated now.</param>
public sealed record AutoRechargeClaim(string Key, DateTimeOffset FirstAttemptAt, DateTimeOffset ClaimedAt, bool RetryReady)
{
    public const string EventType = "auto_recharge.claim";

    /// <summary>Payment metadata naming the claim's key.</summary>
    public const string MetadataKey = "rechargeAttempt";

    /// <summary>
    /// An attempt older than this is taken over (a replica died, or the payment's event never
    /// came); repeating with the same key is safe.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The key is never repeated after this: the service (and Stripe) keep an idempotent
    /// answer for 24 hours, so a later repeat could charge a second time.
    /// </summary>
    public static readonly TimeSpan MaxReuseAge = TimeSpan.FromHours(23);

    public static string EventIdFor(Guid userId) => $"auto_recharge_claim_{userId:D}";

    /// <summary>What to do with the claim found (or not) before charging. Pure.</summary>
    public static ClaimDecision Decide(AutoRechargeClaim? claim, DateTimeOffset now)
    {
        if (claim is null) return ClaimDecision.New;
        if (now - claim.FirstAttemptAt >= MaxReuseAge) return ClaimDecision.Expired;
        if (claim.RetryReady || now - claim.ClaimedAt >= StaleAfter) return ClaimDecision.Reuse;
        return ClaimDecision.Busy;
    }

    /// <summary>Whether the claim stays after an attempt (and is ready for a repeat). Pure.</summary>
    public static (bool Keep, bool RetryReady) After(ServiceChargeOutcome outcome) => outcome switch
    {
        ServiceChargeOutcome.RetryLater => (true, true),
        // Settling: the billing.payment.* event releases it (or it goes stale).
        ServiceChargeOutcome.Pending => (true, false),
        _ => (false, false),
    };
}

public enum ClaimDecision
{
    /// <summary>No charge is running: start one with a new key.</summary>
    New,

    /// <summary>Repeat the unfinished charge with its key.</summary>
    Reuse,

    /// <summary>Another replica (or the payment's event) owns it: do nothing now.</summary>
    Busy,

    /// <summary>
    /// Unfinished for too long to repeat safely: stop auto-recharge and leave the outcome to
    /// the payment's events and an admin.
    /// </summary>
    Expired,
}
