using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One write to the P4 Backoffice service waiting to be sent (transactional outbox).
///
/// Added to the same <c>SaveChangesAsync</c> as the domain change it belongs to, so a
/// rolled-back change never leaves a message behind. Sent by
/// <c>BackofficeOutboxDispatcher</c> outside any request.
/// </summary>
public class BackofficeOutboxItem
{
    /// <summary>Also the <c>Idempotency-Key</c> of every attempt.</summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Handler key, e.g. <c>consent</c>, <c>email.raw</c>, <c>support.ticket</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Request payload as JSON, encrypted with Data Protection; null once sent.</summary>
    public string? PayloadProtected { get; set; }

    /// <summary>One of <see cref="BackofficeOutboxStatus"/>.</summary>
    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = BackofficeOutboxStatus.Pending;

    /// <summary>Sent before non-critical items (2FA, password reset, …).</summary>
    public bool Critical { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Claimed by a dispatcher until then; null or past = free.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Id the service returned for the created resource (e-mail, ticket, …).</summary>
    [MaxLength(64)]
    public string? RemoteId { get; set; }

    [MaxLength(512)]
    public string? LastError { get; set; }

    /// <summary>What the item is about (e.g. <c>user:{id}</c>), for looking it up; never secret.</summary>
    [MaxLength(200)]
    public string? SubjectRef { get; set; }
}

/// <summary>Values of <see cref="BackofficeOutboxItem.Status"/>.</summary>
public static class BackofficeOutboxStatus
{
    /// <summary>Waiting for (another) attempt.</summary>
    public const string Pending = "Pending";

    /// <summary>The service accepted it.</summary>
    public const string Sent = "Sent";

    /// <summary>Given up: refused by the service or out of retries.</summary>
    public const string Dead = "Dead";

    /// <summary>Refused as unauthorised (401/403); requeued by an admin once the key is fixed.</summary>
    public const string Blocked = "Blocked";

    /// <summary>Delivered by the local fallback instead of the service (break-glass e-mail).</summary>
    public const string FallbackSent = "FallbackSent";

    /// <summary>No longer needed.</summary>
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All =
        [Pending, Sent, Dead, Blocked, FallbackSent, Cancelled];
}
