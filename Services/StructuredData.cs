using System.Text.Json;

namespace DotNetSigningServer.Services;

/// <summary>
/// schema.org JSON-LD emitted into every page, telling search engines that
/// "Performance4PDF" is this product published by this company — the entity signal that
/// branded queries are resolved against.
///
/// Built here rather than inline in the Razor layout on purpose: inside a Razor
/// C# expression <c>@@</c> is not an escape, so JSON-LD keys written there come
/// out as <c>@@type</c> and the whole block is silently ignored by crawlers.
/// </summary>
public static class StructuredData
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SiteJsonLd(string origin, string description)
    {
        var trimmed = (origin ?? string.Empty).TrimEnd('/');

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "SoftwareApplication",
            ["name"] = "Performance4PDF",
            ["applicationCategory"] = "DeveloperApplication",
            ["operatingSystem"] = "Any",
            ["url"] = trimmed,
            ["description"] = description,
            ["offers"] = new Dictionary<string, object?>
            {
                ["@type"] = "Offer",
                ["price"] = "0",
                ["priceCurrency"] = "EUR",
                ["url"] = $"{trimmed}/pricing",
            },
            ["publisher"] = new Dictionary<string, object?>
            {
                ["@type"] = "Organization",
                ["name"] = "Performance4PDF",
                ["legalName"] = "Performance4 s.r.o.",
                ["url"] = trimmed,
                ["logo"] = $"{trimmed}/img/logo.png",
                ["email"] = "support@performance4.cz",
                ["sameAs"] = new[] { "https://github.com/Performance4-s-r-o/dotnet-signing-server/" },
            },
        }, Options);
    }
}
