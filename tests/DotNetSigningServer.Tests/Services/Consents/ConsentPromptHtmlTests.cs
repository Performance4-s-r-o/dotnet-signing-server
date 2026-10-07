using System.Text.Encodings.Web;
using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// The wording comes from the backoffice, the markup is this app's. Nothing from the service
/// may reach the page as HTML — a document title is written by a person in an admin, and that
/// is exactly the kind of place a stray tag comes from.
/// </summary>
public class ConsentPromptHtmlTests
{
    private static BackofficeConsentPrompt Prompt(params ConsentPromptSegment[] segments) =>
        new("terms", "terms", "registration", true, 1, "h", "cs", "cs", false, segments);

    private static string Render(BackofficeConsentPrompt prompt)
    {
        using var writer = new StringWriter();
        ConsentPromptHtml.Sentence(prompt).WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    /// <summary>`HtmlEncoder.Default` escapes non-ASCII too, as the rest of this app's consent markup does.</summary>
    private static string E(string text) => HtmlEncoder.Default.Encode(text);

    [Fact]
    public void Builds_the_sentence_with_links_that_open_in_a_new_tab()
    {
        var html = Render(Prompt(
            new ConsentPromptSegment.Text("Souhlasím s "),
            new ConsentPromptSegment.Link("terms", "obchodními podmínkami", "https://legal.test/p4-dotnet/terms", 2, "hash"),
            new ConsentPromptSegment.Text(".")));

        Assert.Equal(
            $"{E("Souhlasím s ")}<a href=\"https://legal.test/p4-dotnet/terms\" target=\"_blank\" rel=\"noopener noreferrer\">{E("obchodními podmínkami")}</a>.",
            html);
    }

    [Fact]
    public void Encodes_everything_the_service_sent()
    {
        var html = Render(Prompt(
            new ConsentPromptSegment.Text("5 < 6 & \"ano\""),
            new ConsentPromptSegment.Link("terms", "<script>alert(1)</script>", "https://legal.test/a?b=1&c=2", 1, "h")));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(E("5 < 6 & \"ano\""), html);
        Assert.Contains("https://legal.test/a?b=1&amp;c=2", html);
    }

    [Fact]
    public void Shows_the_name_but_does_not_link_a_scheme_a_browser_should_not_follow()
    {
        foreach (var url in new[] { "javascript:alert(1)", "data:text/html,<b>x</b>", "file:///etc/passwd", "not a url" })
        {
            var html = Render(Prompt(new ConsentPromptSegment.Link("terms", "Podmínky", url, 1, "h")));
            Assert.Equal(E("Podmínky"), html);
            Assert.DoesNotContain("<a", html);
        }
    }

    [Fact]
    public void Plain_text_is_the_same_sentence_without_the_markup()
    {
        var prompt = Prompt(
            new ConsentPromptSegment.Text("Souhlasím s "),
            new ConsentPromptSegment.Link("terms", "podmínkami", "https://legal.test/terms", 2, "hash"),
            new ConsentPromptSegment.Text("."));

        Assert.Equal("Souhlasím s podmínkami.", ConsentPromptHtml.PlainText(prompt));
    }
}
