using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

public enum UsageRecordStatus
{
    Success = 0,
    Error = 1,
}

public class UsageRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    public Guid? DocumentId { get; set; }

    [ForeignKey(nameof(DocumentId))]
    public Document? Document { get; set; }

    /// <summary>Actual credits debited (base cost × concurrency tier multiplier). 0 for failed requests.</summary>
    [Required]
    public int Count { get; set; } = 1;

    /// <summary>Base cost of the operation before the tier multiplier (for reporting).</summary>
    public int BaseCost { get; set; } = 1;

    /// <summary>Concurrency tier that was active at the time of the operation (1 = base).</summary>
    public int Tier { get; set; } = 1;

    /// <summary>Short operation name (e.g. "sign", "timestamp", "fill-pdf").</summary>
    [MaxLength(32)]
    public string? Operation { get; set; }

    /// <summary>Outcome of the API call. Errors are stored with Count = 0 so billing aggregates remain unaffected.</summary>
    [Required]
    public UsageRecordStatus Status { get; set; } = UsageRecordStatus.Success;

    /// <summary>Short, machine-readable error code (e.g. "TSA_UNREACHABLE", "PDF_TOO_LARGE").</summary>
    [MaxLength(64)]
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable error message. MUST NOT contain PDF bytes, signatures, or other sensitive payload.</summary>
    [MaxLength(512)]
    public string? ErrorMessage { get; set; }

    /// <summary>HTTP status code returned to the client.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>Wall-clock duration of the request in milliseconds.</summary>
    public int? DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
