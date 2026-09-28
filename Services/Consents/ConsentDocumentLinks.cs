namespace DotNetSigningServer.Services.Consents;

/// <summary>Display names and <c>LegalController</c> actions of consent documents, for the views. Pure.</summary>
public static class ConsentDocumentLinks
{
    private static readonly Dictionary<string, (string Action, string NameKey)> Known = new(StringComparer.Ordinal)
    {
        ["terms"] = ("TermsOfService", "LegalTermsOfService"),
        ["privacy"] = ("PrivacyPolicy", "LegalPrivacyPolicy"),
        ["dpa"] = ("DataProcessingAgreement", "LegalDpa"),
        ["sla"] = ("ServiceLevelAgreement", "LegalSla"),
        ["refund"] = ("RefundPolicy", "LegalRefundPolicy"),
        ["cookies"] = ("CookiesPolicy", "LegalCookiesPolicy"),
    };

    /// <summary><c>LegalController</c> action showing the document; null when there is none.</summary>
    public static string? ActionFor(string document) => Known.TryGetValue(document, out var k) ? k.Action : null;

    /// <summary>Resource key of the document's name; null for a document without one.</summary>
    public static string? NameKeyFor(string document) => Known.TryGetValue(document, out var k) ? k.NameKey : null;
}
