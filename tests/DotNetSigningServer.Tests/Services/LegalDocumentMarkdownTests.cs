using Markdig;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The legal-document pipeline is the one place where hand-edited database
/// content is emitted with @Html.Raw. Markdig lets raw inline HTML through by
/// default, so <see cref="MarkdownPipelineBuilder.DisableHtml"/> is what stands
/// between a stray &lt;script&gt; in a row and stored XSS on a public page.
///
/// That guard had no test, which made every Markdig upgrade a leap of faith:
/// the build proves the call still compiles, not that it still does anything.
/// These assertions are about behaviour, so they fail if a future version
/// changes what DisableHtml means or if an extension re-enables raw HTML.
/// </summary>
public class LegalDocumentMarkdownTests
{
    /// <summary>Must mirror LegalDocumentService's pipeline exactly.</summary>
    private static MarkdownPipeline BuildPipeline() =>
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoLinks()
            .DisableHtml()
            .Build();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("Text before <script>alert(1)</script> and after")]
    [InlineData("<div onclick=\"steal()\">click</div>")]
    public void RawHtmlIsNeverEmitted(string markdown)
    {
        var html = Markdown.ToHtml(markdown, BuildPipeline());

        // Only the angle bracket decides whether the browser sees a tag or a
        // piece of text, so that is what gets asserted. An `onerror=` sitting
        // inside `&lt;img src=x onerror=alert(1)&gt;` is inert text, and
        // forbidding the substring would be testing the wrong thing.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div", html, StringComparison.OrdinalIgnoreCase);

        // The tag must have been escaped rather than dropped: silently removing
        // it would also pass the assertions above while losing document text.
        Assert.Contains("&lt;", html);
    }

    [Fact]
    public void OrdinaryMarkdownStillRenders()
    {
        // The guard is worthless if it also breaks the documents it protects.
        var html = Markdown.ToHtml("# Title\n\nSome **bold** text and a [link](https://example.com).", BuildPipeline());

        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void AdvancedExtensionsAreStillOn()
    {
        // UseAdvancedExtensions covers tables among other things; losing it
        // silently would mangle every legal document that uses one.
        var html = Markdown.ToHtml("| a | b |\n|---|---|\n| 1 | 2 |", BuildPipeline());

        Assert.Contains("<table", html);
    }

    [Fact]
    public void AutoLinksAreStillOn()
    {
        var html = Markdown.ToHtml("Visit https://example.com for details.", BuildPipeline());

        Assert.Contains("<a href=\"https://example.com\"", html);
    }
}
