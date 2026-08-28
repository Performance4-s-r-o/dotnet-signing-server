using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Http;

namespace DotNetSigningServer.Tests.Services;

public class CultureUrlsTests
{
    [Fact]
    public void Absolute_KeepsTheCleanUrlForEnglish()
    {
        Assert.Equal("https://p4.example/pricing", CultureUrls.Absolute("https://p4.example", "/pricing", "en"));
        Assert.Equal("https://p4.example/es/pricing", CultureUrls.Absolute("https://p4.example", "/pricing", "es"));
        Assert.Equal("https://p4.example/es", CultureUrls.Absolute("https://p4.example", "/", "es"));
    }

    [Fact]
    public void SwitchUrl_NamesEveryLocaleOutLoud_EnglishIncluded()
    {
        // The clean URL means "no preference", which cannot switch a visitor who is
        // already remembered as speaking something else — so English gets a prefix here.
        Assert.Equal("/en/pricing", CultureUrls.SwitchUrl(string.Empty, "/pricing", "en"));
        Assert.Equal("/en", CultureUrls.SwitchUrl(string.Empty, "/", "en"));
        Assert.Equal("/es/pricing", CultureUrls.SwitchUrl(string.Empty, "/pricing", "es"));
        Assert.Equal("https://p4.example/cs/pricing", CultureUrls.SwitchUrl("https://p4.example/", "/pricing", "cs"));
    }

    [Fact]
    public void SwitchUrl_FallsBackToTheDefaultLocale()
    {
        Assert.Equal("/en/pricing", CultureUrls.SwitchUrl(string.Empty, "/pricing", "fr"));
    }

    [Theory]
    [InlineData("/en/pricing", "/pricing")]
    [InlineData("/EN/pricing", "/pricing")]
    [InlineData("/en", "/")]
    public void TrySplitDefault_StripsAnExplicitEnglishPrefix(string path, string expected)
    {
        Assert.True(CultureUrls.TrySplitDefault(path, out var rest));
        Assert.Equal(expected, rest.Value);
    }

    [Theory]
    [InlineData("/pricing")]
    [InlineData("/english")]
    [InlineData("/es/pricing")]
    public void TrySplitDefault_LeavesEverythingElseAlone(string path)
    {
        Assert.False(CultureUrls.TrySplitDefault(path, out var rest));
        Assert.Equal(path, rest.Value);
    }

    [Theory]
    [InlineData("/es/pricing", "/pricing")]
    [InlineData("/en/pricing", "/pricing")]
    [InlineData("/pricing", "/pricing")]
    [InlineData("/en", "/")]
    public void StripLocale_RemovesEitherFormOfPrefix(string path, string expected)
    {
        Assert.Equal(expected, CultureUrls.StripLocale(path).Value);
    }

    [Fact]
    public void MatchAcceptLanguage_HonoursQualityAndRegions()
    {
        Assert.Equal("de", CultureUrls.MatchAcceptLanguage("de-AT;q=0.9, cs;q=0.4"));
        Assert.Equal("es", CultureUrls.MatchAcceptLanguage("es-ES,es;q=0.9"));
        Assert.Null(CultureUrls.MatchAcceptLanguage("fr-FR,fr;q=0.9"));
        Assert.Null(CultureUrls.MatchAcceptLanguage(""));
    }
}
