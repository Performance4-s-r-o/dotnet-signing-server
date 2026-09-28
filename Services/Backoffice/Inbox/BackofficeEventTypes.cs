namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>Event types of the P4 Backoffice service this product consumes.</summary>
public static class BackofficeEventTypes
{
    public const string DocumentScheduled = "document.scheduled";
    public const string DocumentPublished = "document.published";
    public const string DocumentMinorCorrected = "document.minor_corrected";
    public const string DocumentUnscheduled = "document.unscheduled";
    public const string CookieDeclarationPublished = "cookie_declaration.published";
    public const string PriceScheduled = "price.scheduled";
    public const string PriceEffective = "price.effective";
    public const string PriceUnscheduled = "price.unscheduled";
    public const string PriceSyncFailed = "price.sync_failed";
    public const string EmailBounced = "email.bounced";
    public const string EmailComplained = "email.complained";
    public const string EmailFailed = "email.failed";
    public const string SupportTicketFailed = "support.ticket_failed";

    /// <summary>Sent by the "Test" button of the webhook endpoint; never listed by <c>/v1/events</c>.</summary>
    public const string WebhookTest = "webhook.test";

    /// <summary>
    /// What the webhook endpoint subscribes to and what polling asks for (<c>types=</c>).
    /// Keep in sync with the endpoint's event list in the service admin.
    /// </summary>
    public static readonly IReadOnlyList<string> Subscribed =
    [
        DocumentScheduled, DocumentPublished, DocumentMinorCorrected, DocumentUnscheduled,
        CookieDeclarationPublished,
        PriceScheduled, PriceEffective, PriceUnscheduled, PriceSyncFailed,
        EmailBounced, EmailComplained, EmailFailed,
        SupportTicketFailed,
    ];
}
