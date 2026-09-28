using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>The snapshot migration and upsert on real PostgreSQL (Testcontainers).</summary>
[Trait("Category", "Db")]
public class LegalSnapshotPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public LegalSnapshotPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    [DockerFact]
    public async Task ExistingRows_BecomeManual_AndTheSnapshotRoundTrips()
    {
        await using (var db = _pg.CreateContext())
        {
            await db.LegalDocuments.ExecuteDeleteAsync();
            // A row inserted without the new columns, as rows written before the migration.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"LegalDocuments\" (\"Id\", \"Slug\", \"Locale\", \"Version\", \"Title\", \"Content\", \"EffectiveFrom\", \"IsDraft\", \"CreatedAt\", \"UpdatedAt\") "
                + "VALUES (gen_random_uuid(), 'privacy-policy', 'en', 1, 'Privacy', '# Privacy', now() - interval '1 day', false, now(), now())");
        }

        using var host = new LegalDocsTestHost("On", _pg.Configure);
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(type: "privacy", version: 1, html: "<p>Privacy</p>"));
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(type: "terms", version: 2));
        await host.Refresher.RefreshAsync("privacy", "en", saveSnapshot: true, CancellationToken.None);
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: true, CancellationToken.None);

        var rows = await host.RowsAsync();
        var privacy = rows.Single(r => r.Slug == "privacy-policy");
        Assert.Equal(LegalDocumentSources.Manual, privacy.Source);
        Assert.Equal("# Privacy", privacy.Content);
        Assert.Equal("<p>Privacy</p>", privacy.ContentHtml);
        Assert.Equal(LegalDocsTestHost.Hash("<p>Privacy</p>"), privacy.ContentHash);

        var terms = rows.Single(r => r.Slug == "terms-of-service");
        Assert.Equal(LegalDocumentSources.Backoffice, terms.Source);
        Assert.Equal(64, terms.ContentHash!.Length);

        await using var check = _pg.CreateContext();
        var off = await new DbLegalDocumentSource(check, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbLegalDocumentSource>.Instance)
            .GetAsync("terms-of-service", "en");
        Assert.Null(off);
        var on = await new DbLegalDocumentSource(check, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbLegalDocumentSource>.Instance, includeSnapshots: true)
            .GetAsync("terms-of-service", "en");
        Assert.Equal(2, on!.Version);
    }
}
