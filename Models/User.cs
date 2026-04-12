using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

public class User
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public byte[] PasswordHash { get; set; } = Array.Empty<byte>();

    [Required]
    public byte[] PasswordSalt { get; set; } = Array.Empty<byte>();

    [MaxLength(128)]
    public string? StripeCustomerId { get; set; }

    public bool IsActive { get; set; } = true;
    public bool EmailVerified { get; set; } = false;
    [MaxLength(128)]
    public string? EmailVerificationToken { get; set; }
    [MaxLength(16)]
    public string? EmailOtpCode { get; set; }
    public DateTimeOffset? EmailOtpExpiresAt { get; set; }

    [MaxLength(128)]
    public string? PasswordResetToken { get; set; }
    public DateTimeOffset? PasswordResetExpiresAt { get; set; }

    public int CreditsRemaining { get; set; } = 10;

    public bool AutoRechargeEnabled { get; set; } = false;
    public int AutoRechargeQuantity { get; set; } = 0;
    public decimal AutoRechargePricePer100 { get; set; } = 0m;
    [MaxLength(128)]
    public string? AutoRechargeCancelToken { get; set; }
    public DateTimeOffset? PriceChangeNotifiedAt { get; set; }

    public bool EmailNotificationsEnabled { get; set; } = true;

    /// <summary>Max parallel API operations. NULL = use default (3).</summary>
    public int? MaxConcurrentOperations { get; set; }

    /// <summary>Admin users can access /Admin pages (user management, enterprise toggle).</summary>
    public bool IsAdmin { get; set; } = false;

    /// <summary>
    /// Enterprise users bypass credit checks and are billed manually based on tracked usage.
    /// When enabled: auto-recharge is disabled, saved cards are detached, CreditsRemaining is ignored.
    /// </summary>
    public bool IsEnterprise { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ApiToken> ApiTokens { get; set; } = new List<ApiToken>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
