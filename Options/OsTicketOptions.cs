namespace DotNetSigningServer.Options;

/// <summary>
/// Configuration for the osTicket helpdesk used by the in-app support form
/// (/support). The form — and its nav entry — is only exposed when both
/// <see cref="Url"/> and <see cref="ApiKey"/> are configured; otherwise the
/// page 404s and users are pointed at the support mailbox in the footer.
/// </summary>
public class OsTicketOptions
{
    /// <summary>
    /// Base URL of the osTicket instance exposing /api/tickets.json.
    /// Example: https://osticket.performance4.cz
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// osTicket API key (sent as the X-API-Key header).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Category → osTicket help-topic id map (configure in the osTicket admin).
    /// Keys match the form's category values: signing, templates, billing,
    /// account, other.
    /// </summary>
    public Dictionary<string, string> Topic { get; set; } = new();

    /// <summary>
    /// Per-request HTTP timeout in seconds (default 10).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// True when the helpdesk is fully configured and the support form
    /// should be reachable.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Resolves the help-topic id for a form category, falling back to the
    /// "other" topic and finally to "1".
    /// </summary>
    public string ResolveTopicId(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && Topic.TryGetValue(category, out var topicId)
            && !string.IsNullOrWhiteSpace(topicId))
        {
            return topicId;
        }

        return Topic.TryGetValue("other", out var fallback) && !string.IsNullOrWhiteSpace(fallback)
            ? fallback
            : "1";
    }
}
