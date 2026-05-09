using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DotNetSigningServer.Models;

public class ApiToken
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    [Required]
    [MaxLength(128)]
    public string Label { get; set; } = string.Empty;

    [Required]
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();

    public bool IsBrowserToken { get; set; }

    public string? AllowedOrigins { get; set; }

    public string? AllowedIps { get; set; }

    /// <summary>First 8 chars of the plaintext token, used for fast prefix-based lookup.</summary>
    [MaxLength(8)]
    public string? TokenPrefix { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
