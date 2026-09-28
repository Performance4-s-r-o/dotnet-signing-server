using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One event received from the P4 Backoffice service, by webhook or by polling
/// <c>GET /v1/events</c>, waiting to be (or already) processed.
///
/// <see cref="WebhookId"/> is unique: the same event arriving both ways, or delivered
/// twice, is stored once. Processed by <c>BackofficeInboxProcessor</c> outside any request.
/// </summary>
public class BackofficeWebhookInboxItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><c>webhook-id</c> of the delivery = <c>id</c> of the event in <c>/v1/events</c>.</summary>
    [Required]
    [MaxLength(128)]
    public string WebhookId { get; set; } = string.Empty;

    /// <summary>Event type, e.g. <c>price.scheduled</c>.</summary>
    [Required]
    [MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    /// <summary>The event's <c>data</c> object as received (JSON).</summary>
    [Required]
    public string PayloadJson { get; set; } = "{}";

    /// <summary>One of <see cref="BackofficeInboxSource"/>.</summary>
    [Required]
    [MaxLength(16)]
    public string Source { get; set; } = BackofficeInboxSource.Webhook;

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Null until a handler finished it (or its type has no handler).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Handler runs so far, successful or not.</summary>
    public int Attempts { get; set; }

    /// <summary>When to try (again); null = given up after too many failures.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Last handler failure.</summary>
    [MaxLength(512)]
    public string? Error { get; set; }
}

/// <summary>Values of <see cref="BackofficeWebhookInboxItem.Source"/>.</summary>
public static class BackofficeInboxSource
{
    public const string Webhook = "webhook";
    public const string Poll = "poll";
}
