using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The charge step of auto-recharge through <c>POST /v1/billing/charges</c>
/// (<c>Modules:Billing=On</c>). The service chooses the card (the customer's default, else the
/// first saved one) and stamps the payment; this class keeps one charge per user running
/// across replicas (<see cref="AutoRechargeClaim"/>), sends it with a stable
/// <c>Idempotency-Key</c> and grants the credits of a paid charge. E-mails, the cooldown and
/// stopping auto-recharge stay in <see cref="AutoRechargeService"/>.
/// </summary>
public sealed class BackofficeAutoRecharge
{
    private readonly ApplicationDbContext _db;
    private readonly BackofficeBillingClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficeAutoRecharge> _logger;

    public BackofficeAutoRecharge(ApplicationDbContext db, BackofficeBillingClient client, TimeProvider time, ILogger<BackofficeAutoRecharge> logger)
    {
        _db = db;
        _client = client;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Charges <paramref name="pack"/> to the user's saved card. Null when another charge of the
    /// user is still running (nothing was sent).
    /// </summary>
    public async Task<ServiceCharge?> ChargeAsync(User user, CreditPack pack, CancellationToken cancellationToken = default)
    {
        var (decision, key) = await AcquireAsync(user.Id, cancellationToken);
        if (decision == ClaimDecision.Busy) return null;
        if (decision == ClaimDecision.Expired)
        {
            return new ServiceCharge(ServiceChargeOutcome.Abandoned, null, null, null, "unfinished charge expired");
        }

        var body = new
        {
            CustomerRef = BillingCustomerRef.For(user.Id),
            Items = new[] { new { LookupKey = BackofficePaymentGateway.RequireLookupKey(pack), Quantity = 1 } },
            Currency = pack.StripeCurrency,
            Description = $"Auto-recharge: {pack.Quantity} credits",
            Metadata = new Dictionary<string, string>
            {
                ["type"] = CreditGrants.AutoRechargeType,
                ["userId"] = user.Id.ToString(),
                ["documents"] = pack.Quantity.ToString(),
                [AutoRechargeClaim.MetadataKey] = key!,
            },
        };
        var charge = ServiceCharge.From(await _client.CreateChargeAsync(body, key!, cancellationToken));

        if (charge.Outcome == ServiceChargeOutcome.Succeeded)
        {
            charge = charge with { CreditsGranted = await GrantAsync(user, pack, charge, cancellationToken) };
        }
        await SettleClaimAsync(user.Id, key!, charge.Outcome, cancellationToken);
        return charge;
    }

    /// <summary>
    /// Removes the user's claim when it still belongs to the charge <paramref name="key"/>
    /// names — a late event of an earlier charge never ends a newer one. Saves.
    /// </summary>
    public static async Task<bool> ReleaseAsync(ApplicationDbContext db, Guid userId, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var row = await db.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId), cancellationToken);
        if (row is null || Read(row)?.Key != key) return false;
        db.WebhookEvents.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Grants the paid pack (or finds it granted by its event); false when it cannot be.</summary>
    private async Task<bool> GrantAsync(User user, CreditPack pack, ServiceCharge charge, CancellationToken cancellationToken)
    {
        if (charge.PaymentIntentId is null)
        {
            // Only an invoice of 0 is paid without a payment intent; a pack is never free.
            _logger.LogError("Auto-recharge of user {UserId} succeeded without a payment intent; credits not granted", user.Id);
            return false;
        }
        var key = CreditGrants.AutoRechargeKey(charge.PaymentIntentId);
        var granted = await CreditGrants.TryGrantAsync(_db, key, CreditGrants.AutoRechargeType, user.Id, pack.Quantity, "backoffice",
            () =>
            {
                _db.Payments.Add(new Payment
                {
                    UserId = user.Id,
                    StripePaymentIntentId = charge.PaymentIntentId,
                    AmountCents = (int)(charge.AmountMinor ?? pack.UnitAmountMinor),
                    Currency = (charge.Currency ?? pack.Currency).ToUpperInvariant(),
                    Status = "succeeded",
                });
                return Task.CompletedTask;
            }, cancellationToken);
        return granted || await CreditGrants.IsGrantedAsync(_db, key, cancellationToken);
    }

    /// <summary>The decision and, for New / Reuse, the key to charge with.</summary>
    private async Task<(ClaimDecision Decision, string? Key)> AcquireAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var row = await _db.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId), cancellationToken);
        var claim = row is null ? null : Read(row);
        if (row != null && claim is null)
        {
            // Unreadable (never written by this code): start over.
            _db.WebhookEvents.Remove(row);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var decision = AutoRechargeClaim.Decide(claim, now);
        switch (decision)
        {
            case ClaimDecision.Busy:
                _logger.LogInformation("Auto-recharge of user {UserId}: a charge is still running", userId);
                return (decision, null);

            case ClaimDecision.Expired:
                // Its answer may be gone from the service: repeating the key could charge twice.
                _logger.LogError(
                    "Auto-recharge of user {UserId}: the charge started at {FirstAttemptAt} never finished; not repeated, "
                    + "auto-recharge stopped. Check the payments of the customer in the service (Idempotency-Key {Key})",
                    userId, claim!.FirstAttemptAt, claim.Key);
                _db.WebhookEvents.Remove(row!);
                await _db.SaveChangesAsync(cancellationToken);
                return (decision, null);

            case ClaimDecision.Reuse:
                row!.ReceivedAt = now;
                row.ProcessedAt = null;
                await _db.SaveChangesAsync(cancellationToken);
                return (decision, claim!.Key);

            default:
                var key = Guid.NewGuid().ToString("D");
                _db.WebhookEvents.Add(new WebhookEvent
                {
                    EventId = AutoRechargeClaim.EventIdFor(userId),
                    EventType = AutoRechargeClaim.EventType,
                    PayloadJson = JsonSerializer.Serialize(new { key, first = now }),
                    ReceivedAt = now,
                });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return (decision, key);
                }
                catch (DbUpdateException ex) when (CreditGrants.IsUniqueViolation(ex))
                {
                    CreditGrants.DetachAdded(_db);
                    _logger.LogInformation("Auto-recharge of user {UserId}: another replica started a charge", userId);
                    return (ClaimDecision.Busy, null);
                }
        }
    }

    private async Task SettleClaimAsync(Guid userId, string key, ServiceChargeOutcome outcome, CancellationToken cancellationToken)
    {
        var (keep, retryReady) = AutoRechargeClaim.After(outcome);
        if (!keep)
        {
            await ReleaseAsync(_db, userId, key, cancellationToken);
            return;
        }
        var row = await _db.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId), cancellationToken);
        // Gone or replaced (the event already ended this charge): nothing to keep.
        if (row is null || Read(row)?.Key != key) return;
        row.ProcessedAt = retryReady ? _time.GetUtcNow() : null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The claim stored in <paramref name="row"/>; a row without a readable key is taken over with a new one.</summary>
    internal static AutoRechargeClaim? Read(WebhookEvent row)
    {
        try
        {
            using var doc = JsonDocument.Parse(row.PayloadJson);
            var key = BillingJson.String(doc.RootElement, "key");
            if (string.IsNullOrWhiteSpace(key)) return null;
            var first = BillingJson.Date(doc.RootElement, "first") is { } date
                ? new DateTimeOffset(date, TimeSpan.Zero)
                : row.ReceivedAt;
            return new AutoRechargeClaim(key, first, row.ReceivedAt, row.ProcessedAt.HasValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
