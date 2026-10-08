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
/// <c>Idempotency-Key</c> and grants the credits of a paid charge. E-mails and the cooldown
/// stay in <see cref="AutoRechargeService"/>.
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
        var key = await AcquireAsync(user.Id, cancellationToken);
        if (key is null) return null;

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
            },
        };
        var charge = ServiceCharge.From(await _client.CreateChargeAsync(body, key, cancellationToken));

        if (charge.Outcome == ServiceChargeOutcome.Succeeded)
        {
            await GrantAsync(user, pack, charge, cancellationToken);
        }
        await SettleClaimAsync(user.Id, charge.Outcome, cancellationToken);
        return charge;
    }

    /// <summary>Removes the user's claim (the payment's event arrived). Saves.</summary>
    public static async Task ReleaseAsync(ApplicationDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var row = await db.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId), cancellationToken);
        if (row is null) return;
        db.WebhookEvents.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task GrantAsync(User user, CreditPack pack, ServiceCharge charge, CancellationToken cancellationToken)
    {
        if (charge.PaymentIntentId is null)
        {
            // Only an invoice of 0 is paid without a payment intent; a pack is never free.
            _logger.LogError("Auto-recharge of user {UserId} succeeded without a payment intent; credits not granted", user.Id);
            return;
        }
        var granted = await CreditGrants.TryGrantAsync(_db, CreditGrants.AutoRechargeKey(charge.PaymentIntentId),
            CreditGrants.AutoRechargeType, user.Id, pack.Quantity, "backoffice", cancellationToken);
        if (!granted) return;

        _db.Payments.Add(new Payment
        {
            UserId = user.Id,
            StripePaymentIntentId = charge.PaymentIntentId,
            AmountCents = (int)(charge.AmountMinor ?? pack.UnitAmountMinor),
            Currency = (charge.Currency ?? pack.Currency).ToUpperInvariant(),
            Status = "succeeded",
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The key to charge with, or null when another charge is running.</summary>
    private async Task<string?> AcquireAsync(Guid userId, CancellationToken cancellationToken)
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

        switch (AutoRechargeClaim.Decide(claim, now))
        {
            case ClaimDecision.Busy:
                _logger.LogInformation("Auto-recharge of user {UserId}: a charge is still running", userId);
                return null;

            case ClaimDecision.Reuse:
                row!.ReceivedAt = now;
                row.ProcessedAt = null;
                await _db.SaveChangesAsync(cancellationToken);
                return claim!.Key;

            default:
                var key = Guid.NewGuid().ToString("D");
                _db.WebhookEvents.Add(new WebhookEvent
                {
                    EventId = AutoRechargeClaim.EventIdFor(userId),
                    EventType = AutoRechargeClaim.EventType,
                    PayloadJson = JsonSerializer.Serialize(new { key }),
                    ReceivedAt = now,
                });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return key;
                }
                catch (DbUpdateException ex) when (CreditGrants.IsUniqueViolation(ex))
                {
                    CreditGrants.DetachAdded(_db);
                    _logger.LogInformation("Auto-recharge of user {UserId}: another replica started a charge", userId);
                    return null;
                }
        }
    }

    private async Task SettleClaimAsync(Guid userId, ServiceChargeOutcome outcome, CancellationToken cancellationToken)
    {
        var (keep, retryReady) = AutoRechargeClaim.After(outcome);
        if (!keep)
        {
            await ReleaseAsync(_db, userId, cancellationToken);
            return;
        }
        var row = await _db.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(userId), cancellationToken);
        if (row is null) return;
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
            return new AutoRechargeClaim(key, row.ReceivedAt, row.ProcessedAt.HasValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
