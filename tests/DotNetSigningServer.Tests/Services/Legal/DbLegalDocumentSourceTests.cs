using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>The hand-maintained rows, with and without the service snapshot.</summary>
public class DbLegalDocumentSourceTests
{
    private static ApplicationDbContext Db()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase("legal-db-" + Guid.NewGuid()).Options;
        return new ApplicationDbContext(options);
    }

    private static DbLegalDocumentSource Source(ApplicationDbContext db, bool includeSnapshots) =>
        new(db, NullLogger<DbLegalDocumentSource>.Instance, includeSnapshots);

    [Fact]
    public async Task WithoutSnapshots_ReadsOnlyManualRowsAsMarkdown()
    {
        await using var db = Db();
        var manual = LegalDocsTestHost.Manual(version: 1);
        manual.ContentHtml = "<p>service text next to the manual row</p>";
        db.LegalDocuments.AddRange(manual, LegalDocsTestHost.Snapshot(version: 2));
        await db.SaveChangesAsync();

        var result = await Source(db, includeSnapshots: false).GetAsync("terms-of-service", "en");

        Assert.NotNull(result);
        Assert.Equal(1, result!.Version);
        Assert.Contains("<h1", result.ContentHtml);
        Assert.Contains("Manual terms", result.ContentHtml);
    }

    [Fact]
    public async Task WithSnapshots_ReadsTheNewestRowAndPrefersServiceHtml()
    {
        await using var db = Db();
        db.LegalDocuments.AddRange(LegalDocsTestHost.Manual(version: 1), LegalDocsTestHost.Snapshot(version: 2));
        await db.SaveChangesAsync();

        var result = await Source(db, includeSnapshots: true).GetAsync("terms-of-service", "en");

        Assert.Equal(2, result!.Version);
        Assert.Equal("<p>Snapshot terms</p>", result.ContentHtml);
    }

    [Fact]
    public async Task WithSnapshots_ManualRowWithServiceHtml_ShowsTheServiceHtml()
    {
        await using var db = Db();
        var manual = LegalDocsTestHost.Manual(version: 1);
        manual.ContentHtml = "<p>from the service</p>";
        db.LegalDocuments.Add(manual);
        await db.SaveChangesAsync();

        var result = await Source(db, includeSnapshots: true).GetAsync("terms-of-service", "en");

        Assert.Equal("<p>from the service</p>", result!.ContentHtml);
    }

    [Fact]
    public async Task OnlySnapshotRows_AreInvisibleWithoutSnapshots()
    {
        await using var db = Db();
        db.LegalDocuments.Add(LegalDocsTestHost.Snapshot());
        await db.SaveChangesAsync();

        Assert.Null(await Source(db, includeSnapshots: false).GetAsync("terms-of-service", "en"));
    }

    [Fact]
    public async Task Czech_FallsBackToEnglish_AndOtherLanguagesReadEnglish()
    {
        await using var db = Db();
        db.LegalDocuments.Add(LegalDocsTestHost.Manual(locale: "en"));
        await db.SaveChangesAsync();
        var source = Source(db, includeSnapshots: false);

        Assert.Equal("en", (await source.GetAsync("terms-of-service", "cs"))!.Locale);
        Assert.Equal("en", (await source.GetAsync("terms-of-service", "de"))!.Locale);
    }

    [Fact]
    public async Task DraftsAndFutureRows_AreNotServed()
    {
        await using var db = Db();
        var draft = LegalDocsTestHost.Manual(version: 2);
        draft.IsDraft = true;
        db.LegalDocuments.AddRange(
            LegalDocsTestHost.Manual(version: 1),
            draft,
            LegalDocsTestHost.Manual(version: 3, effectiveFrom: DateTimeOffset.UtcNow.AddDays(10)));
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Source(db, includeSnapshots: true).GetAsync("terms-of-service", "en"))!.Version);
    }

    [Fact]
    public async Task DatabaseFailure_ReturnsNull()
    {
        var db = Db();
        await db.DisposeAsync();

        Assert.Null(await Source(db, includeSnapshots: true).GetAsync("terms-of-service", "en"));
    }
}
