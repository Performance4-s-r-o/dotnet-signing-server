using System.Text.Encodings.Web;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace DotNetSigningServer.TagHelpers;

/// <summary>
/// Keeps the locale prefix on hand-written links.
///
/// CulturePrefixMiddleware moves "/cs" into PathBase, which is enough for links
/// built by Url.Action or RedirectToAction. Views mostly use literal hrefs
/// (href="/pricing"), which nothing would rewrite — a Czech visitor clicking one
/// would silently fall back to English. This runs last on every &lt;a&gt; and
/// &lt;form&gt; and prefixes root-relative URLs that don't carry the prefix yet.
/// </summary>
[HtmlTargetElement("a", Attributes = "href")]
[HtmlTargetElement("form", Attributes = "action")]
public class CulturePrefixTagHelper : TagHelper
{
    /// <summary>Runs after the built-in anchor/form helpers have produced their URL.</summary>
    public override int Order => 1000;

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    /// <summary>
    /// Marks a link that already names its own locale — the language switcher.
    /// Without it, the switcher's English entry ("/pricing") would be rewritten
    /// back to the current locale and never actually switch anything.
    /// </summary>
    public const string OptOutAttribute = "data-locale-link";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (output.Attributes.ContainsName(OptOutAttribute))
        {
            output.Attributes.RemoveAll(OptOutAttribute);
            return;
        }

        var pathBase = ViewContext.HttpContext.Request.PathBase.Value;
        if (string.IsNullOrEmpty(pathBase) || pathBase == "/")
        {
            return;
        }

        var name = output.TagName == "form" ? "action" : "href";
        if (!output.Attributes.TryGetAttribute(name, out var attribute))
        {
            return;
        }

        var url = Stringify(attribute.Value);
        if (url == null || !ShouldPrefix(url, pathBase))
        {
            return;
        }

        var prefixed = url == "/" ? pathBase : pathBase + url;
        output.Attributes.SetAttribute(new TagHelperAttribute(name, prefixed, attribute.ValueStyle));
    }

    /// <summary>
    /// Only root-relative in-app URLs, and only when they aren't already prefixed.
    /// Protocol-relative "//host/…" is external and must stay untouched.
    /// </summary>
    private static bool ShouldPrefix(string url, string pathBase)
    {
        if (url.Length == 0 || url[0] != '/' || url.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (url.Equals(pathBase, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(pathBase + "/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(pathBase + "?", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Any locale prefix counts as already-localized — the language switcher
        // links to the other languages by their own prefix.
        if (CultureUrls.TrySplit(url.Split('?')[0], out _, out _))
        {
            return false;
        }

        // The REST surface is version- and locale-neutral. /api/docs is a page, not the API.
        if (url.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("/api/docs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string s => s,
        HtmlString h => h.Value,
        IHtmlContent content => Render(content),
        _ => value.ToString(),
    };

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }
}
