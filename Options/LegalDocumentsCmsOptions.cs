namespace DotNetSigningServer.Options;

/// <summary>
/// Configuration for the v0 portal acting as the source-of-truth CMS for
/// legal/policy documents (Terms, Privacy, DPA, …). When BaseUrl is empty
/// or the CMS is unreachable, the LegalController falls back to the local
/// static Razor views.
/// </summary>
public class LegalDocumentsCmsOptions
{
    /// <summary>
    /// Base URL of the v0 portal exposing /api/legal-documents/{slug}.
    /// Example: https://app.p4pdf.cz
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Cache TTL in seconds for fetched documents (default 300 = 5 min).
    /// </summary>
    public int CacheSeconds { get; set; } = 300;

    /// <summary>
    /// Per-request HTTP timeout in seconds (default 5).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;
}
