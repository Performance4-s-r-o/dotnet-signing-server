using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// States of a paid API operation (#77). Stored and sent on the wire as these strings.
///
/// <c>pending → running → completed | failed | failed_uncertain</c>. A retry with the same
/// operation id moves <c>failed</c> and <c>failed_uncertain</c> back to <c>running</c>;
/// <c>completed</c> is final and a retry gets the stored result instead.
/// </summary>
public static class ApiOperationState
{
    /// <summary>Row exists, nothing reserved, no work started.</summary>
    public const string Pending = "pending";

    /// <summary>Credits reserved, work under way (until <see cref="ApiOperation.LeaseUntil"/>).</summary>
    public const string Running = "running";

    /// <summary>Result stored and credits charged — in one transaction.</summary>
    public const string Completed = "completed";

    /// <summary>Failed before anything irreversible; reservation released, retry is safe.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// Failed after an external step whose outcome is unknown (TSA or model timeout, a worker
    /// that died mid-run). Nothing was delivered and the reservation is released, so the
    /// customer is not charged; the external side (a TSA token, a model call) may have
    /// happened. A retry runs the work again.
    /// </summary>
    public const string FailedUncertain = "failed_uncertain";
}

/// <summary>
/// One paid API operation, keyed by the caller's <c>Idempotency-Key</c> (the portal's
/// <c>operationId</c>) or, for <c>/api/sign</c>, by the presign it consumes.
///
/// Credits are reserved (debited) when the operation starts and either kept when it
/// completes or returned when it fails. Completion stores the result and charges in the
/// same transaction, so a result is never delivered unpaid and a charge never exists
/// without a result to hand back on retry.
/// </summary>
public class ApiOperation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The caller's <c>Idempotency-Key</c>; a generated id when none was sent. Unique per user.</summary>
    [Required]
    [MaxLength(128)]
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Whether <see cref="OperationId"/> came from the caller (and so can be retried by it).</summary>
    public bool ClientKey { get; set; }

    /// <summary>Endpoint name, e.g. <c>sign</c>, <c>timestamp</c>, <c>fill-pdf</c>.</summary>
    [Required]
    [MaxLength(32)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the request; a key reused for a different request is refused.</summary>
    [Required]
    [MaxLength(64)]
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary><c>/api/sign</c> only: the presign this operation consumes. Unique.</summary>
    [MaxLength(64)]
    public string? PresignId { get; set; }

    /// <summary>One of <see cref="ApiOperationState"/>.</summary>
    [Required]
    [MaxLength(24)]
    public string State { get; set; } = ApiOperationState.Pending;

    /// <summary>Times the work was started; every state change is conditional on it.</summary>
    public int Attempts { get; set; }

    /// <summary>A running attempt not finished by then is treated as dead.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Base price before the concurrency tier.</summary>
    public int BaseCost { get; set; }

    public int Tier { get; set; } = 1;

    /// <summary>Credits taken from the balance and held while running; 0 otherwise (and for Enterprise).</summary>
    public int CreditsReserved { get; set; }

    /// <summary>Credits charged on completion (base × tier; Enterprise records it without a debit).</summary>
    public int CreditsCharged { get; set; }

    [MaxLength(64)]
    public string? ErrorCode { get; set; }

    public int? ResultStatusCode { get; set; }

    /// <summary>The JSON body carries the PDF in <c>result</c>, so <c>Accept: application/pdf</c> can get the bytes.</summary>
    public bool ResultIsPdf { get; set; }

    /// <summary>Response body as JSON, encrypted with Data Protection; null once purged.</summary>
    public byte[]? ResultProtected { get; set; }

    /// <summary>Headers sent with a raw PDF answer (JSON object), e.g. <c>X-PDF-Conformance</c>.</summary>
    [MaxLength(512)]
    public string? ResultPdfHeaders { get; set; }

    /// <summary>When <see cref="ResultProtected"/> is purged. The row itself lives longer, for state queries.</summary>
    public DateTimeOffset? ResultExpiresAt { get; set; }

    /// <summary>
    /// The portal's tenant that started it (<c>X-P4-Tenant</c>, #78). The portal holds one account
    /// for every tenant: a replay or a state read for another tenant is refused. Null for other callers.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
