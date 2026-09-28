namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Route slugs of <c>LegalController</c> ↔ document types of the P4 Backoffice service.
/// Same keys as the import of this product's documents into the service. Pure.
/// </summary>
public static class LegalSlugMap
{
    private static readonly (string Slug, string Type)[] Pairs =
    [
        ("terms-of-service", "terms"),
        ("privacy-policy", "privacy"),
        ("data-processing-agreement", "dpa"),
        ("service-level-agreement", "sla"),
        ("refund-policy", "refund"),
        ("cookies-policy", "cookies"),
        ("open-source-notices", "oss"),
        ("license", "license"),
    ];

    private static readonly Dictionary<string, string> BySlug =
        Pairs.ToDictionary(p => p.Slug, p => p.Type, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ByType =
        Pairs.ToDictionary(p => p.Type, p => p.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every service type this product shows, in footer order.</summary>
    public static readonly IReadOnlyList<string> Types = Pairs.Select(p => p.Type).ToArray();

    /// <summary>Service type of a route slug; null for a slug the service does not know.</summary>
    public static string? TypeFor(string? slug) =>
        slug != null && BySlug.TryGetValue(slug, out var type) ? type : null;

    /// <summary>Route slug of a service type; null for a type this product does not show.</summary>
    public static string? SlugFor(string? type) =>
        type != null && ByType.TryGetValue(type, out var slug) ? slug : null;
}

/// <summary>
/// Languages legal documents exist in: Czech and English. Every other UI language reads the
/// English text (as before the integration).
/// </summary>
public static class LegalLocales
{
    public const string Default = "en";

    /// <summary>Locales kept in the snapshot.</summary>
    public static readonly IReadOnlyList<string> Snapshot = ["en", "cs"];

    public static string Normalize(string? locale) =>
        string.Equals(locale, "cs", StringComparison.OrdinalIgnoreCase) ? "cs" : Default;
}
