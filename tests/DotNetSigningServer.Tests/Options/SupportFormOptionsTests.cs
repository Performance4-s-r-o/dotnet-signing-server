using DotNetSigningServer.Options;

namespace DotNetSigningServer.Tests.Options;

/// <summary>
/// The public support form is the service's, framed on the contact page. Without a product
/// slug the page is what it always was, so nothing is rendered half-configured.
/// </summary>
public class SupportFormOptionsTests
{
    [Fact]
    public void Without_a_product_nothing_is_rendered()
    {
        var o = new P4BackofficeProductOptions.SupportFormOptions();

        Assert.Null(o.ScriptSrc);
        Assert.Null(o.PageUrl);
        Assert.Null(o.DataProduct);
    }

    [Fact]
    public void Defaults_to_the_public_legal_viewer()
    {
        var o = new P4BackofficeProductOptions.SupportFormOptions { Product = "p4-dotnet" };

        Assert.Equal("https://legal.performance4.cz/v1/support-embed.js", o.ScriptSrc);
        Assert.Equal("https://legal.performance4.cz/p4-dotnet/support", o.PageUrl);
        Assert.Equal("p4-dotnet", o.DataProduct);
    }

    [Fact]
    public void A_local_viewer_and_a_trailing_slash_are_both_fine()
    {
        var o = new P4BackofficeProductOptions.SupportFormOptions { Url = "http://localhost:3200/", Product = " p4-dotnet " };

        Assert.Equal("http://localhost:3200/v1/support-embed.js", o.ScriptSrc);
        Assert.Equal("http://localhost:3200/p4-dotnet/support", o.PageUrl);
        Assert.Equal("p4-dotnet", o.DataProduct);
    }

    // The slug reaches a URL, so it is escaped rather than trusted.
    [Fact]
    public void A_slug_that_is_not_one_cannot_break_out_of_the_path()
    {
        var o = new P4BackofficeProductOptions.SupportFormOptions { Product = "../admin" };

        Assert.Equal("https://legal.performance4.cz/..%2Fadmin/support", o.PageUrl);
    }
}
