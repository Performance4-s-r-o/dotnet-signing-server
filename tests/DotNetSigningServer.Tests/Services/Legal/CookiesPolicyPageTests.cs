using System.Net;
using System.Text.RegularExpressions;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Consents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary><c>/Legal/CookiesPolicy</c> rendered by the whole app (throwaway PostgreSQL; the service is never called).</summary>
[Trait("Category", "Db")]
public class CookiesPolicyPageTests : IClassFixture<SignUpPostgresFixture>
{
    private readonly SignUpPostgresFixture _fixture;

    public CookiesPolicyPageTests(SignUpPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedDocument(LegalDocumentRendered? document) : ILegalDocumentSource
    {
        public Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default) =>
            Task.FromResult(slug == "cookies-policy" ? document : null);
    }

    private sealed class FixedDeclaration(CookieDeclarationSnapshot? declaration) : ICookieDeclarationSource
    {
        public List<string> Locales { get; } = new();

        public Task<CookieDeclarationSnapshot?> GetAsync(string locale, CancellationToken cancellationToken = default)
        {
            Locales.Add(locale);
            return Task.FromResult(declaration);
        }
    }

    private async Task<string> PageAsync(string path, Action<IServiceCollection>? configure = null)
    {
        await using var factory = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "Off", configure);
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static List<string> TableCookieNames(string html) =>
        Regex.Matches(WebUtility.HtmlDecode(html), @"<td><code>([^<]+)</code></td>").Select(m => m.Groups[1].Value).ToList();

    [Theory]
    [InlineData("/Legal/CookiesPolicy", "Strictly necessary")]
    [InlineData("/cs/Legal/CookiesPolicy", "nezbytné")]
    public async Task RazorFallback_ListsExactlyTheAuditedCookies_AndNoAnalytics(string path, string necessaryText)
    {
        var html = WebUtility.HtmlDecode(await PageAsync(path));

        Assert.DoesNotContain("Google Analytics", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(necessaryText, html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuditedCookies.Names, TableCookieNames(html));
    }

    [Fact]
    public async Task ServiceDocument_ShowsTheDeclarationUnderTheText()
    {
        var declaration = new FixedDeclaration(new CookieDeclarationSnapshot(
            3, "cs", null, [],
            [new CookieDeclarationCookie("svc_cookie", "Performance4", "necessary", "Z deklarace <b>služby</b>", "Relace", "first")],
            null, DateTimeOffset.UnixEpoch));
        var html = await PageAsync("/cs/Legal/CookiesPolicy", services =>
        {
            services.RemoveAll<ILegalDocumentSource>();
            services.AddScoped<ILegalDocumentSource>(_ => new FixedDocument(new LegalDocumentRendered(
                "cookies-policy", "cs", 3, "Zásady cookies", null, null, "<p>Text ze služby</p>")));
            services.RemoveAll<ICookieDeclarationSource>();
            services.AddSingleton<ICookieDeclarationSource>(declaration);
        });

        Assert.Contains("Text ze služby", WebUtility.HtmlDecode(html));
        Assert.Equal(["svc_cookie"], TableCookieNames(html));
        // Declaration texts are encoded, never raw HTML.
        Assert.Contains("Z deklarace &lt;b&gt;", html);
        Assert.DoesNotContain("<b>slu", html);
        Assert.Equal(["cs"], declaration.Locales);
    }

    [Fact]
    public async Task ServiceDocument_WithoutDeclaration_ShowsTheAuditedCookies()
    {
        var html = await PageAsync("/Legal/CookiesPolicy", services =>
        {
            services.RemoveAll<ILegalDocumentSource>();
            services.AddScoped<ILegalDocumentSource>(_ => new FixedDocument(new LegalDocumentRendered(
                "cookies-policy", "en", 3, "Cookies Policy", null, null, "<p>Service text</p>")));
        });

        Assert.Contains("Service text", html);
        Assert.Equal(AuditedCookies.Names, TableCookieNames(html));
    }
}
