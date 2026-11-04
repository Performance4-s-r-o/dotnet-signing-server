using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

public class Payment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    public Guid? InvoiceId { get; set; }

    [ForeignKey(nameof(InvoiceId))]
    public Invoice? Invoice { get; set; }

    [MaxLength(128)]
    public string? StripePaymentIntentId { get; set; }

    public int AmountCents { get; set; }

    /// <summary>ISO 4217 code as the billing service reported it; no default (#175).</summary>
    [Required]
    [MaxLength(8)]
    public required string Currency { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
