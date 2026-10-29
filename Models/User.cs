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

    /// <summary>
    /// PBKDF2 iteration count this hash was produced with. A hash made with an older
    /// count is rehashed on the owner's next successful sign-in.
    /// </summary>
    public int PasswordIterations { get; set; }

    [MaxLength(128)]
    public string? StripeCustomerId { get; set; }

    public bool IsActive { get; set; } = true;
    public bool EmailVerified { get; set; } = false;

    /// <summary>SHA-256 hex of the emailed verification token — never the token itself.</summary>
    [MaxLength(128)]
    public string? EmailVerificationToken { get; set; }
    public DateTimeOffset? EmailVerificationExpiresAt { get; set; }

    /// <summary>SHA-256 of the code, hex — 64 chars, not the six digits.</summary>
    [MaxLength(128)]
    public string? EmailOtpCode { get; set; }
    public DateTimeOffset? EmailOtpExpiresAt { get; set; }

    /// <summary>
    /// SHA-256 hex of the sign-in challenge (#80): issued with the e-mailed code after the
    /// password step and held by the browser in an HttpOnly cookie. The code is accepted only
    /// together with it, so the 2FA step belongs to the sign-in that asked for it, never to an
    /// address typed into the form. NULL while no sign-in is waiting (and for a re-auth code).
    /// </summary>
    [MaxLength(128)]
    public string? LoginChallengeHash { get; set; }

    /// <summary>Wrong codes against the current <see cref="EmailOtpCode"/>; at the limit the code is void.</summary>
    public int EmailOtpFailedAttempts { get; set; }

    /// <summary>SHA-256 hex of the emailed reset token — never the token itself.</summary>
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

    /// <summary>
    /// Price-list version of the service (<c>Modules:Pricing=On</c>) this user was last told about
    /// (<c>price.scheduled</c>); at most one notice per version. Reset when the version takes
    /// effect or is taken back. Ignored while Pricing is Off.
    /// </summary>
    public int? PriceChangeNotifiedVersion { get; set; }

    public bool EmailNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Language of the last sign-up or sign-in (<c>cs</c>, <c>en</c>, …); e-mails sent in the
    /// background (auto-recharge, payment, price change) use it. NULL = <c>en</c>.
    /// </summary>
    [MaxLength(MaxLocaleLength)]
    public string? Locale { get; set; }

    public const int MaxLocaleLength = 8;

    /// <summary>E-mail language of this user: <see cref="Locale"/>, else <c>en</c>.</summary>
    [NotMapped]
    public string EmailLocale => string.IsNullOrWhiteSpace(Locale) ? "en" : Locale;

    /// <summary>
    /// When the e-mail service last reported a hard bounce or a spam complaint for this
    /// address (<c>email.bounced</c> / <c>email.complained</c>). NULL = none.
    /// </summary>
    public DateTimeOffset? EmailBouncedAt { get; set; }

    /// <summary>Max parallel API operations. NULL = use default (3).</summary>
    public int? MaxConcurrentOperations { get; set; }

    /// <summary>
    /// Seconds to wait for a free concurrency slot when the limit is reached.
    /// 0 = reject immediately with 429. NULL = use BillingOptions.ConcurrencyQueueTimeoutSeconds.
    /// </summary>
    public int? ConcurrencyQueueTimeoutSeconds { get; set; }

    /// <summary>Admin users can access /Admin pages (user management, enterprise toggle).</summary>
    public bool IsAdmin { get; set; } = false;

    /// <summary>
    /// Enterprise users bypass credit checks and are billed manually based on tracked usage.
    /// When enabled: auto-recharge is disabled, saved cards are detached, CreditsRemaining is ignored.
    /// </summary>
    public bool IsEnterprise { get; set; } = false;

    /// <summary>
    /// Whether this account may call <c>/api/seal</c>. Since #122 that endpoint has no
    /// certificate to lend (always <c>422 SEAL_CERTIFICATE_REQUIRED</c> once the grant passes);
    /// the flag is kept until the owner decides whether to drop it. It used to gate
    /// signing with the operator's OWN certificate rather than one the caller supplied.
    ///
    /// Off by default, and deliberately not tied to credits: every other endpoint
    /// signs with something the caller brought, so paying for it is the whole
    /// check. Sealing lends the operator's identity, and a document that carries
    /// it verifies as theirs in any reader — so it is granted per account, never
    /// earned by signing up.
    /// </summary>
    public bool SealAllowed { get; set; } = false;

    /// <summary>
    /// The trusted internal caller's service account (#78): its server token is accepted only on
    /// the internal listener (<c>INTERNAL_API_PORT</c>), and only there are its tenant headers
    /// believed and the attachment debit skippable. On the public port the token is refused.
    /// Off by default; only an administrator sets it, never the account itself.
    /// </summary>
    public bool TrustedCaller { get; set; } = false;

    /// <summary>
    /// Timestamp when enterprise mode was last enabled. Usage shown in the enterprise billing
    /// view is filtered to records created at or after this time — usage before the switch is
    /// considered separately (pay-as-you-go credits). NULL = enterprise was never enabled, or
    /// the flag predates this tracking (treated as "since account creation").
    /// </summary>
    public DateTimeOffset? EnterpriseEnabledAt { get; set; }

    /// <summary>
    /// When the account was erased (GDPR art. 17, #217). The row stays as a tombstone without
    /// personal data, because invoices and payments (kept 10 years) and usage records point at
    /// it; see <c>AccountErasureService</c>. NULL = a live account.
    /// </summary>
    public DateTimeOffset? ErasedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ApiToken> ApiTokens { get; set; } = new List<ApiToken>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
