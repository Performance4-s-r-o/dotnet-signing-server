using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class WebhookEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string EventId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    [Required]
    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    [MaxLength(512)]
    public string? Error { get; set; }
}
