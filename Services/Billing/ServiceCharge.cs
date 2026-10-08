namespace DotNetSigningServer.Services.Billing;

/// <summary>What an off-session charge through the billing API came to.</summary>
public enum ServiceChargeOutcome
{
    /// <summary>Paid; credits are granted now.</summary>
    Succeeded,

    /// <summary>Still settling (<c>processing</c>, or a status this code does not know): the <c>billing.payment.*</c> event decides.</summary>
    Pending,

    /// <summary>The bank wants the customer (3DS); nobody is there to answer, so it counts as failed.</summary>
    RequiresAction,

    /// <summary>The card was declined (<c>failed</c>, a 200).</summary>
    Declined,

    /// <summary>No answer, 5xx, 429 or a request still in progress: repeat later with the same key.</summary>
    RetryLater,

    /// <summary>Refused (4xx: no card, daily limit, not configured, …): nothing happened; a new attempt gets a new key.</summary>
    Refused,

    /// <summary>
    /// Not sent: the previous charge stayed unfinished too long to repeat its key safely
    /// (<see cref="ClaimDecision.Expired"/>). Auto-recharge is stopped.
    /// </summary>
    Abandoned,
}

/// <summary>
/// The answer of <c>POST /v1/billing/charges</c>, and how it maps to <see cref="ServiceChargeOutcome"/>. Pure.
/// </summary>
/// <param name="PaymentIntentId">Stripe payment intent; null without one.</param>
/// <param name="Reason">Decline code or problem code, for logs and the failure e-mail.</param>
public sealed record ServiceCharge(ServiceChargeOutcome Outcome, string? PaymentIntentId, long? AmountMinor, string? Currency, string? Reason)
{
    /// <summary>The credits of this payment are on the account (granted now or earlier by its event).</summary>
    public bool CreditsGranted { get; init; }

    public static ServiceCharge From(BillingApiResult result)
    {
        if (BillingRetryPolicy.RetryWithSameKey(result))
        {
            return new(ServiceChargeOutcome.RetryLater, null, null, null, result.ProblemCode);
        }
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Body))
        {
            return new(ServiceChargeOutcome.Refused, null, null, null, result.StripeCode ?? result.ProblemCode ?? $"http_{result.Status}");
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
            var root = doc.RootElement;
            var status = BillingJson.String(root, "status");
            var outcome = status switch
            {
                "succeeded" => ServiceChargeOutcome.Succeeded,
                "requires_action" => ServiceChargeOutcome.RequiresAction,
                "failed" => ServiceChargeOutcome.Declined,
                _ => ServiceChargeOutcome.Pending,
            };
            return new(outcome,
                BillingJson.String(root, "id"),
                BillingJson.Long(root, "amount"),
                BillingJson.String(root, "currency"),
                outcome == ServiceChargeOutcome.Declined ? BillingJson.String(root, "decline_code") ?? "declined" : status);
        }
        catch (System.Text.Json.JsonException)
        {
            // Accepted but unreadable: the event is the source of truth.
            return new(ServiceChargeOutcome.Pending, null, null, null, null);
        }
    }
}
