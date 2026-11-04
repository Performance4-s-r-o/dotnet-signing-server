using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

public class Invoice
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    [MaxLength(128)]
    public string? StripeInvoiceId { get; set; }

    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }

    public int AmountCents { get; set; }

    /// <summary>ISO 4217 code as the billing service reported it; no default (#175).</summary>
    [Required]
    [MaxLength(8)]
    public required string Currency { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
