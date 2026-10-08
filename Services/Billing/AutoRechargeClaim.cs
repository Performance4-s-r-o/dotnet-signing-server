namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The one auto-recharge charge of a user that may be running, shared by every replica: a
/// row in <c>WebhookEvents</c> (unique <c>EventId</c>) holding the charge's
/// <c>Idempotency-Key</c>. While it exists no second charge with another key starts; a charge
/// that got no answer is repeated with the same key, so the service answers with the same
/// payment instead of charging again.
/// </summary>
/// <param name="Key">Idempotency-Key of the charge.</param>
/// <param name="ClaimedAt">When the current attempt started.</param>
/// <param name="RetryReady">The last attempt got no final answer and may be repeated now.</param>
public sealed record AutoRechargeClaim(string Key, DateTimeOffset ClaimedAt, bool RetryReady)
{
    public const string EventType = "auto_recharge.claim";

    /// <summary>
    /// An attempt older than this is taken over (a replica died, or the payment's event never
    /// came); repeating with the same key is safe.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static string EventIdFor(Guid userId) => $"auto_recharge_claim_{userId:D}";

    /// <summary>What to do with the claim found (or not) before charging. Pure.</summary>
    public static ClaimDecision Decide(AutoRechargeClaim? claim, DateTimeOffset now)
    {
        if (claim is null) return ClaimDecision.New;
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
}
