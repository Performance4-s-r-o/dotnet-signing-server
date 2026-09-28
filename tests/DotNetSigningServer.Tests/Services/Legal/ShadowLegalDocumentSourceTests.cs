using System.Net;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Legal;

public class ShadowLegalDocumentSourceTests
{
    [Fact]
    public async Task RendersTheManualRow_ComparesInTheBackground_AndWritesNothing()
    {
        using var host = new LegalDocsTestHost("Shadow");
        await host.SeedAsync(LegalDocsTestHost.Manual());
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson());

        using var scope = host.Services.CreateScope();
        var source = Assert.IsType<ShadowLegalDocumentSource>(scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>());
        var result = await source.GetAsync("terms-of-service", "en");
        await source.PendingComparison!;

        Assert.Equal("Terms (manual)", result!.Title);
        Assert.Equal(1, host.RequestCount);
        var row = Assert.Single(await host.RowsAsync());
        Assert.Null(row.ContentHtml);
    }

    [Fact]
    public async Task ComparesOncePerTtl()
    {
        using var host = new LegalDocsTestHost("Shadow");
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        using var scope = host.Services.CreateScope();
        var source = (ShadowLegalDocumentSource)scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>();
        Assert.Null(await source.GetAsync("terms-of-service", "en"));
        var first = source.PendingComparison;
        await first!;
        await source.GetAsync("terms-of-service", "en");

        Assert.Same(first, source.PendingComparison);
        Assert.Equal(1, host.RequestCount);
    }

    private static BackofficeDocumentContent Remote(int version = 2, string title = "Terms", string locale = "en", DateTimeOffset? from = null) =>
        new("terms", "Terms", true, version, "effective", "material", from ?? new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            null, locale, false, title, "<p>x</p>", LegalDocsTestHost.Hash("<p>x</p>"), "");

    private static LegalDocumentRendered Local(int version = 2, string title = "Terms", string locale = "en", DateTimeOffset? from = null) =>
        new("terms-of-service", locale, version, title, null, from ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "<h1>x</h1>");

    [Fact]
    public void Diff_IgnoresHtmlAndTimeOfDay()
    {
        Assert.Empty(LegalShadowDiff.Describe(Local(), Remote()));
    }

    [Fact]
    public void Diff_ReportsVersionTitleDateAndLocale()
    {
        var differences = LegalShadowDiff.Describe(
            Local(version: 1, title: "Old", locale: "cs", from: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Remote());

        Assert.Equal(4, differences.Count);
        Assert.Contains(differences, d => d.StartsWith("version"));
        Assert.Contains(differences, d => d.StartsWith("title"));
        Assert.Contains(differences, d => d.StartsWith("effective_from"));
        Assert.Contains(differences, d => d.StartsWith("locale"));
    }

    [Fact]
    public void Diff_ReportsAStaticViewLocally()
    {
        Assert.Single(LegalShadowDiff.Describe(null, Remote()));
    }
}
