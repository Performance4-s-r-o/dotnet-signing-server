using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One payment turned into credits, kept forever (#79). <see cref="GrantKey"/> is unique — the
/// checkout session id, or <c>auto_recharge_&lt;payment intent&gt;</c> — and so is
/// <see cref="PaymentIntentId"/> when known. The row is inserted with
/// <c>ON CONFLICT DO NOTHING</c> in the same transaction as the credits, so the confirm page
/// and the <c>billing.*</c> events grant one payment exactly once,
/// however late or often they are redelivered. Unlike an idempotency key of the backoffice
/// (kept 24 h) or a webhook log, nothing ever expires or deletes these rows.
/// </summary>
public class CreditGrant
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string GrantKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? PaymentIntentId { get; set; }

    /// <summary>Null only on rows backfilled from old <c>WebhookEvents</c> without a readable user.</summary>
    public Guid? UserId { get; set; }

    public int Credits { get; set; }

    /// <summary><c>confirm-page</c>, <c>backoffice</c>, and in older rows <c>stripe-webhook</c>, <c>auto-recharge</c>, <c>legacy</c>.</summary>
    [Required]
    [MaxLength(32)]
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }
}
