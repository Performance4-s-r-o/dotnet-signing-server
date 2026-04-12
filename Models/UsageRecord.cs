using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

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

    /// <summary>Actual credits debited (base cost × concurrency tier multiplier).</summary>
    [Required]
    public int Count { get; set; } = 1;

    /// <summary>Base cost of the operation before the tier multiplier (for reporting).</summary>
    public int BaseCost { get; set; } = 1;

    /// <summary>Concurrency tier that was active at the time of the operation (1 = base).</summary>
    public int Tier { get; set; } = 1;

    /// <summary>Short operation name (e.g. "sign", "timestamp", "fill-pdf").</summary>
    [MaxLength(32)]
    public string? Operation { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
