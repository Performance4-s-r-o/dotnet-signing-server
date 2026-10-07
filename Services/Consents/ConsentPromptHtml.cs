using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using DotNetSigningServer.Services.Backoffice.Consents;

namespace DotNetSigningServer.Services.Consents;

/// <summary>
/// The sentence next to a consent checkbox, built from the segments the backoffice publishes.
///
/// The service decides the wording, this app decides the markup: text is encoded, a link
/// becomes <c>&lt;a target="_blank" rel="noopener noreferrer"&gt;</c>. Nothing from the
/// service is ever emitted as HTML, so a document title can never carry markup into the page.
/// </summary>
public static class ConsentPromptHtml
{
    /// <summary>Only a document the browser can open; anything else is shown as plain text.</summary>
    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);

    /// <summary>The whole sentence; safe to emit raw because every part of it is encoded here.</summary>
    public static IHtmlContent Sentence(BackofficeConsentPrompt prompt)
    {
        var html = new StringBuilder();
        foreach (var segment in prompt.Segments)
        {
            switch (segment)
            {
                case ConsentPromptSegment.Text text:
                    html.Append(HtmlEncoder.Default.Encode(text.Value));
                    break;
                case ConsentPromptSegment.Link link when IsSafeUrl(link.Url):
                    html.Append("<a href=\"")
                        .Append(HtmlEncoder.Default.Encode(link.Url))
                        .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                        .Append(HtmlEncoder.Default.Encode(link.Title))
                        .Append("</a>");
                    break;
                case ConsentPromptSegment.Link link:
                    // A URL this app will not link to still has a name worth showing.
                    html.Append(HtmlEncoder.Default.Encode(link.Title));
                    break;
            }
        }
        return new HtmlString(html.ToString());
    }

    /// <summary>The same sentence without markup — for a title attribute, a log or an e-mail in plain text.</summary>
    public static string PlainText(BackofficeConsentPrompt prompt) =>
        string.Concat(prompt.Segments.Select(s => s switch
        {
            ConsentPromptSegment.Text text => text.Value,
            ConsentPromptSegment.Link link => link.Title,
            _ => "",
        }));
}
