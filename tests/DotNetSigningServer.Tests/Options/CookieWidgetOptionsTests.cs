using DotNetSigningServer.Options;

namespace DotNetSigningServer.Tests.Options;

public class CookieWidgetOptionsTests
{
    [Fact]
    public void NoKey_NoWidget()
    {
        Assert.Null(new P4BackofficeProductOptions.CookieWidgetOptions().ScriptSrc);
        Assert.Null(new P4BackofficeProductOptions.CookieWidgetOptions { Url = "https://x.example", PublishableKey = " " }.ScriptSrc);
    }

    [Fact]
    public void LoadsFromTheLegalViewerByDefault()
    {
        var o = new P4BackofficeProductOptions.CookieWidgetOptions { PublishableKey = "p4pk_live_abc" };
        Assert.Equal("https://legal.performance4.cz/v1/widget.js", o.ScriptSrc);
    }

    [Fact]
    public void TheUrlCanBeChanged()
    {
        var o = new P4BackofficeProductOptions.CookieWidgetOptions { PublishableKey = "p4pk_test_abc", Url = " http://localhost:3200/ " };
        Assert.Equal("http://localhost:3200/v1/widget.js", o.ScriptSrc);
    }
}
