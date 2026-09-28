using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Services.Consents;

/// <summary>Links to consent documents for the sign-up form, the consent page and the notice banner.</summary>
public static class ConsentHtml
{
    /// <summary>The localized name of a document, falling back to its title or type.</summary>
    public static string Name(IStringLocalizer localizer, string document, string? title = null) =>
        ConsentDocumentLinks.NameKeyFor(document) is { } key ? localizer[key].Value
        : !string.IsNullOrWhiteSpace(title) ? title
        : document;

    /// <summary>
    /// <c>&lt;a href="/cs/Legal/TermsOfService" target="_blank"&gt;Podmínky použití (verze 2)&lt;/a&gt;</c>;
    /// the URL keeps the page's language prefix. Encoded; safe to emit raw.
    /// </summary>
    public static string Link(IUrlHelper url, IStringLocalizer localizer, string document, string? title, int? version)
    {
        var name = Name(localizer, document, title);
        var text = version is { } v ? string.Format(localizer["LegalDocumentVersion"].Value, name, v) : name;
        var encoded = HtmlEncoder.Default.Encode(text);
        var action = ConsentDocumentLinks.ActionFor(document);
        var href = action is null ? null : url.Action(action, "Legal");
        return href is null
            ? encoded
            : $"<a href=\"{HtmlEncoder.Default.Encode(href)}\" target=\"_blank\" rel=\"noopener\">{encoded}</a>";
    }

    /// <summary>Several links joined with the localized "and".</summary>
    public static string Links(IUrlHelper url, IStringLocalizer localizer, IEnumerable<ConsentDocumentVersion> documents, bool showVersions)
    {
        var links = documents.Select(d => Link(url, localizer, d.Document, d.Title, showVersions ? d.Version : null)).ToList();
        if (links.Count <= 1) return links.FirstOrDefault() ?? "";
        var and = HtmlEncoder.Default.Encode(localizer["ListAnd"].Value);
        return string.Join(", ", links.Take(links.Count - 1)) + and + links[^1];
    }

    /// <summary>A localized sentence with <paramref name="encodedArguments"/> (already encoded HTML) in place of <c>{0}</c>, <c>{1}</c>, ….</summary>
    public static IHtmlContent Sentence(IStringLocalizer localizer, string key, params string[] encodedArguments) =>
        new HtmlString(string.Format(HtmlEncoder.Default.Encode(localizer[key].Value), encodedArguments));
}
