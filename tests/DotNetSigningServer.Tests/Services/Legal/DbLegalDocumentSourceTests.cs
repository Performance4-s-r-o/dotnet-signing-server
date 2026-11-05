using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>The snapshot of the service's last answer; nothing else is a text source.</summary>
public class DbLegalDocumentSourceTests
{
    private static ApplicationDbContext Db()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase("legal-db-" + Guid.NewGuid()).Options;
        return new ApplicationDbContext(options);
    }

    private static DbLegalDocumentSource Source(ApplicationDbContext db) =>
        new(db, NullLogger<DbLegalDocumentSource>.Instance);

    [Fact]
    public async Task ReadsTheNewestSnapshotRow()
    {
        await using var db = Db();
        db.LegalDocuments.AddRange(LegalDocsTestHost.Snapshot(version: 1), LegalDocsTestHost.Snapshot(version: 2));
        await db.SaveChangesAsync();

        var result = await Source(db).GetAsync("terms-of-service", "en");

        Assert.Equal(2, result!.Version);
        Assert.Equal("<p>Snapshot terms</p>", result.ContentHtml);
    }

    [Fact]
    public async Task RowWithoutServiceHtml_IsNotAText()
    {
        await using var db = Db();
        db.LegalDocuments.Add(LegalDocsTestHost.Manual());
        await db.SaveChangesAsync();

        Assert.Null(await Source(db).GetAsync("terms-of-service", "en"));
    }

    [Fact]
    public async Task Czech_FallsBackToEnglish_AndOtherLanguagesReadEnglish()
    {
        await using var db = Db();
        db.LegalDocuments.Add(LegalDocsTestHost.Snapshot(locale: "en"));
        await db.SaveChangesAsync();
        var source = Source(db);

        Assert.Equal("en", (await source.GetAsync("terms-of-service", "cs"))!.Locale);
        Assert.Equal("en", (await source.GetAsync("terms-of-service", "de"))!.Locale);
    }

    [Fact]
    public async Task DraftsAndFutureRows_AreNotServed()
    {
        await using var db = Db();
        var draft = LegalDocsTestHost.Snapshot(version: 2);
        draft.IsDraft = true;
        var future = LegalDocsTestHost.Snapshot(version: 3);
        future.EffectiveFrom = DateTimeOffset.UtcNow.AddDays(10);
        db.LegalDocuments.AddRange(LegalDocsTestHost.Snapshot(version: 1), draft, future);
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Source(db).GetAsync("terms-of-service", "en"))!.Version);
    }

    [Fact]
    public async Task DatabaseFailure_ReturnsNull()
    {
        var db = Db();
        await db.DisposeAsync();

        Assert.Null(await Source(db).GetAsync("terms-of-service", "en"));
    }
}
