using System.Text;
using System.Xml.Linq;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers;

/// <summary>
/// Serves robots.txt and sitemap.xml. Both are generated rather than static so
/// the host is always right (dev, staging, production) and new API reference
/// pages appear in the sitemap automatically.
/// </summary>
public class SeoController : Controller
{
    private readonly AppOptions _appOptions;

    public SeoController(IOptions<AppOptions> appOptions)
    {
        _appOptions = appOptions.Value;
    }

    /// <summary>Public marketing and legal pages, in rough priority order.</summary>
    private static readonly (string Path, string ChangeFreq, string Priority)[] StaticPages =
    {
        ("/", "weekly", "1.0"),
        ("/pricing", "weekly", "0.9"),
        ("/api/docs", "weekly", "0.9"),
        ("/contact", "monthly", "0.5"),
        ("/Legal", "monthly", "0.3"),
        ("/Legal/TermsOfService", "monthly", "0.3"),
        ("/Legal/PrivacyPolicy", "monthly", "0.3"),
        ("/Legal/DataProcessingAgreement", "monthly", "0.3"),
        ("/Legal/ServiceLevelAgreement", "monthly", "0.3"),
        ("/Legal/RefundPolicy", "monthly", "0.3"),
        ("/Legal/CookiesPolicy", "monthly", "0.3"),
        ("/Legal/OpenSourceNotices", "monthly", "0.2"),
        ("/Legal/License", "monthly", "0.2"),
    };

    /// <summary>Locales that get their own sitemap entry and hreflang alternates.</summary>
    private static readonly string[] Locales = { "en", "cs", "de", "es" };

    [HttpGet("/robots.txt")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Robots()
    {
        var origin = Origin();
        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        // Everything below is behind authentication or is a one-off action —
        // no search value, and crawling it only burns crawl budget.
        sb.AppendLine("Disallow: /Account/");
        sb.AppendLine("Disallow: /Admin");
        sb.AppendLine("Disallow: /ApiTokens");
        sb.AppendLine("Disallow: /Billing");
        sb.AppendLine("Disallow: /Requests");
        sb.AppendLine("Disallow: /support");
        sb.AppendLine("Disallow: /templates");
        sb.AppendLine("Disallow: /template-builder");
        sb.AppendLine("Disallow: /debug/");
        sb.AppendLine("Disallow: /swagger");
        sb.AppendLine();
        sb.AppendLine($"Sitemap: {origin}/sitemap.xml");

        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }

    [HttpGet("/sitemap.xml")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Sitemap()
    {
        var origin = Origin();
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";

        var paths = StaticPages.ToList();
        paths.AddRange(ApiDocsCatalog.All($"{origin}/api")
            .Select(e => ($"/api/docs/{e.Slug}", "monthly", "0.8")));

        var urlset = new XElement(ns + "urlset",
            new XAttribute(XNamespace.Xmlns + "xhtml", xhtml.NamespaceName));

        foreach (var (path, changeFreq, priority) in paths)
        {
            var url = new XElement(ns + "url",
                new XElement(ns + "loc", LocaleUrl(origin, path, "en")),
                new XElement(ns + "changefreq", changeFreq),
                new XElement(ns + "priority", priority));

            // Declare every language variant of this page to each other, so a
            // crawler that finds one finds them all.
            foreach (var locale in Locales)
            {
                url.Add(new XElement(xhtml + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("hreflang", locale),
                    new XAttribute("href", LocaleUrl(origin, path, locale))));
            }
            url.Add(new XElement(xhtml + "link",
                new XAttribute("rel", "alternate"),
                new XAttribute("hreflang", "x-default"),
                new XAttribute("href", LocaleUrl(origin, path, "en"))));

            urlset.Add(url);
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), urlset);
        return Content(document.Declaration + Environment.NewLine + document, "application/xml; charset=utf-8");
    }

    /// <summary>English keeps the clean URL; other locales carry ?culture=xx.</summary>
    private static string LocaleUrl(string origin, string path, string locale) =>
        locale == "en" ? $"{origin}{path}" : $"{origin}{path}?culture={locale}";

    /// <summary>
    /// The configured public origin — not the request host. A sitemap or
    /// canonical that echoed whatever Host arrived would advertise internal
    /// hostnames and split the same page across several URLs.
    /// </summary>
    private string Origin() => _appOptions.BaseUrl;
}
