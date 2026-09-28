using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Tests.Services.Legal;

public class LegalSlugMapTests
{
    [Theory]
    [InlineData("terms-of-service", "terms")]
    [InlineData("privacy-policy", "privacy")]
    [InlineData("data-processing-agreement", "dpa")]
    [InlineData("service-level-agreement", "sla")]
    [InlineData("refund-policy", "refund")]
    [InlineData("cookies-policy", "cookies")]
    [InlineData("open-source-notices", "oss")]
    [InlineData("license", "license")]
    public void MapsEverySlugToTheImportedTypeAndBack(string slug, string type)
    {
        Assert.Equal(type, LegalSlugMap.TypeFor(slug));
        Assert.Equal(slug, LegalSlugMap.SlugFor(type));
    }

    [Fact]
    public void Types_AreTheEightImportedDocuments()
    {
        Assert.Equal(new[] { "terms", "privacy", "dpa", "sla", "refund", "cookies", "oss", "license" }, LegalSlugMap.Types);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("imprint")]
    [InlineData("terms")]
    public void UnknownSlug_HasNoType(string? slug) => Assert.Null(LegalSlugMap.TypeFor(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("health_data")]
    [InlineData("terms-of-service")]
    public void UnknownType_HasNoSlug(string? type) => Assert.Null(LegalSlugMap.SlugFor(type));

    [Theory]
    [InlineData("cs", "cs")]
    [InlineData("CS", "cs")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData("es", "en")]
    [InlineData(null, "en")]
    public void Locales_AreCzechOrEnglish(string? locale, string expected) =>
        Assert.Equal(expected, LegalLocales.Normalize(locale));
}
