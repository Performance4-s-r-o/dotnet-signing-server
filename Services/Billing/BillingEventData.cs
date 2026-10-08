using System.Text.Json;

namespace DotNetSigningServer.Services.Billing;

/// <summary>A paid credit purchase read from <c>billing.checkout.completed</c>.</summary>
public sealed record CheckoutPurchase(
    string SessionId,
    Guid UserId,
    int Documents,
    bool AutoRecharge,
    long? AmountTotal,
    string? Currency,
    string? PaymentIntentId,
    string? StripeCustomerId);

/// <summary>An auto-recharge payment read from <c>billing.payment.succeeded</c> / <c>.failed</c>.</summary>
/// <param name="AttemptKey">Key of the auto-recharge claim that sent the charge (metadata <c>rechargeAttempt</c>).</param>
public sealed record AutoRechargePayment(string PaymentIntentId, Guid UserId, int Documents, long? Amount, string? Currency, string? FailureCode, string? AttemptKey);

/// <summary>
/// Reads the <c>billing.*</c> event data this product acts on. Pure. Every reader refuses
/// (null + reason) rather than guess: an event of another flow, or one whose customer is not
/// the user named in its metadata, grants nothing.
/// </summary>
public static class BillingEventData
{
    /// <summary><c>billing.checkout.completed</c> of a paid credit purchase.</summary>
    public static CheckoutPurchase? Purchase(JsonElement data, out string? reason)
    {
        reason = null;
        var metadata = BillingJson.StringMap(data, "metadata");
        if (BillingJson.String(data, "kind") is { } kind && kind != "payment")
        {
            reason = $"mode {kind}";
            return null;
        }
        if (BillingJson.String(data, "payment_status") != "paid")
        {
            reason = "not paid";
            return null;
        }
        if (BillingJson.String(data, "stripe_session_id") is not { Length: > 0 } sessionId)
        {
            reason = "no session id";
            return null;
        }
        if (!TryOwner(data, metadata, out var userId, out reason)) return null;
        if (!metadata.TryGetValue("documents", out var documentsText) || !int.TryParse(documentsText, out var documents) || documents <= 0)
        {
            reason = "no documents in metadata";
            return null;
        }

        return new CheckoutPurchase(
            sessionId,
            userId,
            documents,
            metadata.TryGetValue("autoRecharge", out var auto) && bool.TryParse(auto, out var on) && on,
            BillingJson.Long(data, "amount_total"),
            BillingJson.String(data, "currency"),
            BillingJson.String(data, "stripe_payment_intent_id"),
            BillingJson.String(data, "stripe_customer_id"));
    }

    /// <summary><c>billing.payment.*</c> of an auto-recharge charge (metadata <c>type=auto_recharge</c>).</summary>
    public static AutoRechargePayment? AutoRecharge(JsonElement data, out string? reason)
    {
        reason = null;
        var metadata = BillingJson.StringMap(data, "metadata");
        if (!metadata.TryGetValue("type", out var type) || type != CreditGrants.AutoRechargeType)
        {
            reason = "not an auto-recharge";
            return null;
        }
        if (BillingJson.String(data, "stripe_payment_intent_id") is not { Length: > 0 } paymentIntentId)
        {
            reason = "no payment intent";
            return null;
        }
        if (!TryOwner(data, metadata, out var userId, out reason)) return null;
        if (!metadata.TryGetValue("documents", out var documentsText) || !int.TryParse(documentsText, out var documents) || documents <= 0)
        {
            reason = "no documents in metadata";
            return null;
        }
        return new AutoRechargePayment(paymentIntentId, userId, documents,
            BillingJson.Long(data, "amount"), BillingJson.String(data, "currency"), BillingJson.String(data, "failure_code"),
            metadata.GetValueOrDefault(AutoRechargeClaim.MetadataKey));
    }

    /// <summary>The user of <c>billing.payment_method.detached</c>; null for a customer this product did not create.</summary>
    public static Guid? DetachedFrom(JsonElement data) =>
        BillingCustomerRef.TryParse(BillingJson.String(data, "customer_ref"), out var userId) ? userId : null;

    /// <summary>The metadata's <c>userId</c>, which must be the user the event's customer belongs to.</summary>
    private static bool TryOwner(JsonElement data, IReadOnlyDictionary<string, string> metadata, out Guid userId, out string? reason)
    {
        reason = null;
        if (!metadata.TryGetValue("userId", out var text) || !Guid.TryParse(text, out userId))
        {
            userId = Guid.Empty;
            reason = "no userId in metadata";
            return false;
        }
        if (BillingJson.String(data, "customer_ref") != BillingCustomerRef.For(userId))
        {
            reason = "customer_ref does not match userId";
            return false;
        }
        return true;
    }
}
