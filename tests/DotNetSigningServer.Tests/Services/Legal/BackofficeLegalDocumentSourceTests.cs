using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>Docs module On, with the service stubbed by <see cref="StubServiceHandler"/>.</summary>
public class BackofficeLegalDocumentSourceTests
{
    private static async Task<(LegalDocumentRendered? Result, BackofficeLegalDocumentSource Source)> GetAsync(
        LegalDocsTestHost host, string slug = "terms-of-service", string locale = "en")
    {
        using var scope = host.Services.CreateScope();
        var source = Assert.IsType<BackofficeLegalDocumentSource>(scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>());
        var result = await source.GetAsync(slug, locale);
        if (source.PendingRefresh != null) await source.PendingRefresh;
        return (result, source);
    }

    [Fact]
    public async Task Ok_UpsertsTheSnapshot_AndServesTheServiceText()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson());

        // Cold start, empty snapshot: the page falls back to Razor, the fetch runs in the background.
        var (first, _) = await GetAsync(host);
        Assert.Null(first);

        var request = host.Request(0);
        Assert.Equal("https://backoffice.test/v1/documents/terms?format=html&locale=en", request.RequestUri!.ToString());

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal("terms-of-service", row.Slug);
        Assert.Equal("en", row.Locale);
        Assert.Equal(2, row.Version);
        Assert.Equal(LegalDocumentSources.Backoffice, row.Source);
        Assert.Equal("terms", row.TypeKey);
        Assert.Equal("", row.Content);
        Assert.False(row.IsDraft);
        Assert.Equal("<h1>Terms</h1><p>Version two.</p>", row.ContentHtml);
        Assert.Equal(LegalDocsTestHost.Hash(row.ContentHtml!), row.ContentHash);
        Assert.Equal("material", row.ChangeKind);
        Assert.Equal(host.Time.Now, row.FetchedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), row.EffectiveFrom);

        // Next page: straight from memory, version and date as the service says, no new request.
        var (second, _) = await GetAsync(host);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Version);
        Assert.Equal("Terms of Service", second.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), second.EffectiveFrom);
        Assert.Equal("Clearer refund rules.", second.Summary);
        Assert.Equal("<h1>Terms</h1><p>Version two.</p>", second.ContentHtml);
        Assert.Equal(1, host.RequestCount);
    }

    [Fact]
    public async Task ServiceDown_WithSnapshot_ServesTheSnapshot()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Manual(), LegalDocsTestHost.Snapshot());
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        var (result, _) = await GetAsync(host);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Version);
        Assert.Equal("Terms (snapshot)", result.Title);
        Assert.Equal("<p>Snapshot terms</p>", result.ContentHtml);
    }

    [Fact]
    public async Task ServiceDown_WithoutSnapshot_ReturnsNullForTheRazorView()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        var (result, _) = await GetAsync(host);

        Assert.Null(result);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task ThePage_NeverWaitsForTheService()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Snapshot());
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.Responses.Enqueue(_ => release.Task);

        using var scope = host.Services.CreateScope();
        var source = (BackofficeLegalDocumentSource)scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>();
        var result = await source.GetAsync("terms-of-service", "en");

        Assert.NotNull(result);
        Assert.Equal("<p>Snapshot terms</p>", result!.ContentHtml);
        Assert.False(source.PendingRefresh!.IsCompleted);

        release.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(await source.PendingRefresh);
    }

    [Fact]
    public async Task AfterAFailure_TheServiceIsLeftAloneForTheBackoff()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Snapshot());
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        await GetAsync(host);
        await GetAsync(host);
        await GetAsync(host);
        Assert.Equal(1, host.RequestCount);

        host.Time.Advance(LegalDocumentRefresher.FailureBackoff);
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        await GetAsync(host);
        Assert.Equal(2, host.RequestCount);
    }

    [Fact]
    public async Task NotPublishedType_IsNotAnOutage_AndIsNotAskedAgainWithinTheTtl()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Manual(slug: "open-source-notices"));
        host.EnqueueStatus(HttpStatusCode.NotFound);

        var (oss, _) = await GetAsync(host, slug: "open-source-notices");
        await GetAsync(host, slug: "open-source-notices");

        Assert.Equal("Terms (manual)", oss!.Title);
        Assert.Equal(1, host.RequestCount);
        Assert.False(host.Refresher.InBackoff);

        // Other documents are still fetched.
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson());
        await GetAsync(host);
        Assert.Equal(2, host.RequestCount);
    }

    [Fact]
    public async Task StaleCopy_IsServedAndRevalidatedWithTheETag()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(), etag: "\"v2\"");
        await GetAsync(host);

        host.Time.Advance(host.Cache.Ttl + TimeSpan.FromSeconds(1));
        host.EnqueueStatus(HttpStatusCode.NotModified);
        var (stale, _) = await GetAsync(host);

        Assert.Equal(2, stale!.Version);
        Assert.Equal(2, host.RequestCount);
        Assert.Equal("\"v2\"", host.Request(1).Headers.IfNoneMatch.Single().ToString());

        // 304 refreshed the entry: fresh again, no further request.
        await GetAsync(host);
        Assert.Equal(2, host.RequestCount);
    }

    [Fact]
    public async Task StaleCopy_IsServedWhileTheServiceIsDown()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson());
        await GetAsync(host);

        host.Time.Advance(TimeSpan.FromDays(2));
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var (result, _) = await GetAsync(host);

        Assert.NotNull(result);
        Assert.Equal("<h1>Terms</h1><p>Version two.</p>", result!.ContentHtml);
    }

    [Fact]
    public async Task CzechMissing_EnglishFallbackIsStoredAsEnglish()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(locale: "en", fallback: true));

        var (first, _) = await GetAsync(host, locale: "cs");
        Assert.Null(first);
        Assert.Contains("locale=cs", host.Request(0).RequestUri!.Query);

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal("en", row.Locale);

        var (second, _) = await GetAsync(host, locale: "cs");
        Assert.Equal("en", second!.Locale);
    }

    [Fact]
    public async Task Czech_SnapshotFallsBackToEnglish()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Snapshot(locale: "en"));
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        var (result, _) = await GetAsync(host, locale: "cs");

        Assert.Equal("en", result!.Locale);
        Assert.Equal("<p>Snapshot terms</p>", result.ContentHtml);
    }

    [Fact]
    public async Task ManualRowOfTheSameVersion_KeepsItsOwnColumns()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Manual(version: 2));
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 2));

        await GetAsync(host);

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal(LegalDocumentSources.Manual, row.Source);
        Assert.Equal("# Manual terms", row.Content);
        Assert.Equal("Terms (manual)", row.Title);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), row.EffectiveFrom);
        Assert.Equal("<h1>Terms</h1><p>Version two.</p>", row.ContentHtml);
        Assert.Equal("terms", row.TypeKey);
    }

    [Fact]
    public async Task MinorCorrection_UpdatesTheSnapshotRowInPlace()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson());
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: true, CancellationToken.None);

        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(html: "<p>Typo fixed.</p>", changeKind: "minor", title: "Terms"), etag: "\"etag-2\"");
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: true, CancellationToken.None);

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal("<p>Typo fixed.</p>", row.ContentHtml);
        Assert.Equal(LegalDocsTestHost.Hash("<p>Typo fixed.</p>"), row.ContentHash);
        Assert.Equal("Terms", row.Title);
        Assert.Equal(2, row.Version);
    }

    [Fact]
    public async Task NewVersion_AddsARow_AndTheOldOneStaysOnRecord()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 2));
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: true, CancellationToken.None);
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 3, effectiveFrom: "2026-09-20T00:00:00Z", html: "<p>v3</p>"));
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: true, CancellationToken.None);

        var rows = await host.RowsAsync();
        Assert.Equal(new[] { 2, 3 }, rows.Select(r => r.Version));

        // The snapshot fallback picks the newest one.
        host.Cache.Remove("terms", "en");
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var (result, _) = await GetAsync(host);
        Assert.Equal(3, result!.Version);
    }

    [Fact]
    public async Task UnparsableResponse_CountsAsAnOutage()
    {
        using var host = new LegalDocsTestHost();
        await host.SeedAsync(LegalDocsTestHost.Snapshot());
        host.Service.Enqueue(HttpStatusCode.OK, "<html>proxy error</html>");

        var (result, _) = await GetAsync(host);

        Assert.Equal("<p>Snapshot terms</p>", result!.ContentHtml);
        Assert.Single(await host.RowsAsync());
    }

    [Fact]
    public void SnapshotHash_IsOnlyStoredWhenItIsASha256Digest()
    {
        Assert.Null(LegalDocumentsSnapshotWriter.NormaliseHash("sha256:abc"));
        Assert.Null(LegalDocumentsSnapshotWriter.NormaliseHash(null));
        Assert.Equal(new string('a', 64), LegalDocumentsSnapshotWriter.NormaliseHash(new string('A', 64)));
    }
}
