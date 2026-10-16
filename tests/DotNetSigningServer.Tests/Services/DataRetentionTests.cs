using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#217 D-2: retention of Stripe webhook payloads and document metadata, and log redaction.</summary>
public class DataRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_DeletesOldWebhookEventsAndDocuments_KeepsRecentAndClaims()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        var user = TestHelpers.CreateTestUser("a@example.cz");
        db.Users.Add(user);
        db.WebhookEvents.AddRange(
            new WebhookEvent { EventId = "evt_old", PayloadJson = "{}", ReceivedAt = Now.AddDays(-31) },
            new WebhookEvent { EventId = "evt_new", PayloadJson = "{}", ReceivedAt = Now.AddDays(-29) },
            new WebhookEvent { EventId = AutoRechargeClaim.EventIdFor(user.Id), PayloadJson = "{}", ReceivedAt = Now.AddDays(-60) });
        var oldDoc = new Document { UserId = user.Id, CreatedAt = Now.AddDays(-91) };
        var newDoc = new Document { UserId = user.Id, CreatedAt = Now.AddDays(-89) };
        db.Documents.AddRange(oldDoc, newDoc);
        var usage = new UsageRecord { UserId = user.Id, DocumentId = oldDoc.Id };
        db.UsageRecords.Add(usage);
        await db.SaveChangesAsync();

        var (events, documents) = await DataRetentionService.RunAsync(db, Now);

        Assert.Equal(1, events);
        Assert.Equal(1, documents);
        Assert.Equal(new[] { "evt_new", AutoRechargeClaim.EventIdFor(user.Id) }.Order(), db.WebhookEvents.Select(w => w.EventId).AsEnumerable().Order());
        Assert.Equal(newDoc.Id, Assert.Single(db.Documents).Id);
        var kept = await db.UsageRecords.SingleAsync();
        Assert.Null(kept.DocumentId); // billing record stays, only the link is cut
    }

    [Fact]
    public async Task Run_Twice_IsIdempotent()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        db.WebhookEvents.Add(new WebhookEvent { EventId = "evt_old", PayloadJson = "{}", ReceivedAt = Now.AddDays(-40) });
        await db.SaveChangesAsync();
        Assert.Equal((1, 0), await DataRetentionService.RunAsync(db, Now));
        Assert.Equal((0, 0), await DataRetentionService.RunAsync(db, Now));
    }

    [Fact]
    public void LogRedaction_Email_IsStableCaseInsensitiveAndHidesTheAddress()
    {
        var a = LogRedaction.Email("Jana.Novak@Example.com");
        Assert.Matches("^email:[0-9a-f]{8}$", a);
        Assert.Equal(a, LogRedaction.Email(" jana.novak@example.com "));
        Assert.NotEqual(a, LogRedaction.Email("other@example.com"));
        Assert.Equal("email:none", LogRedaction.Email(null));
    }

    [Fact]
    public void LogRedaction_Text_ReplacesAddressesIncludingEncodedAndUnicode()
    {
        var text = LogRedaction.Text("ticket for jan@firma.cz failed; ?e=jan%40firma.cz and řehoř@škola.cz");
        Assert.DoesNotContain("firma", text);
        Assert.DoesNotContain("škola", text);
        Assert.Equal(3, text.Split("email:").Length - 1);
    }
}

/// <summary>The relational path (ExecuteDelete/ExecuteUpdate) against a real PostgreSQL.</summary>
[Trait("Category", "Db")]
public class DataRetentionPostgresTests : IClassFixture<DotNetSigningServer.Tests.Services.Backoffice.Outbox.OutboxPostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly DotNetSigningServer.Tests.Services.Backoffice.Outbox.OutboxPostgresFixture _pg;

    public DataRetentionPostgresTests(DotNetSigningServer.Tests.Services.Backoffice.Outbox.OutboxPostgresFixture pg) => _pg = pg;

    [DotNetSigningServer.Tests.Services.Backoffice.Outbox.DockerFact]
    public async Task Run_OnPostgres_DeletesOnlyOldRows_AndKeepsUsageAndClaims()
    {
        await using var db = _pg.CreateContext();
        var user = TestHelpers.CreateTestUser($"ret-{Guid.NewGuid():N}@example.cz");
        db.Users.Add(user);
        var other = TestHelpers.CreateTestUser($"ret-{Guid.NewGuid():N}@example.cz");
        db.Users.Add(other);
        var oldDoc = new Document { UserId = user.Id, CreatedAt = Now.AddDays(-91) };
        var newDoc = new Document { UserId = other.Id, CreatedAt = Now.AddDays(-1) };
        db.Documents.AddRange(oldDoc, newDoc);
        db.UsageRecords.AddRange(new UsageRecord { UserId = user.Id, DocumentId = oldDoc.Id }, new UsageRecord { UserId = other.Id, DocumentId = newDoc.Id });
        var oldId = "evt_ret_old_" + Guid.NewGuid().ToString("N");
        db.WebhookEvents.AddRange(
            new WebhookEvent { EventId = oldId, PayloadJson = "{}", ReceivedAt = Now.AddDays(-45) },
            new WebhookEvent { EventId = AutoRechargeClaim.EventIdFor(user.Id), PayloadJson = "{}", ReceivedAt = Now.AddDays(-45) });
        await db.SaveChangesAsync();

        var (events, documents) = await DataRetentionService.RunAsync(db, Now);

        Assert.True(events >= 1);
        Assert.True(documents >= 1);
        Assert.False(await db.WebhookEvents.AnyAsync(w => w.EventId == oldId));
        Assert.True(await db.WebhookEvents.AnyAsync(w => w.EventId == AutoRechargeClaim.EventIdFor(user.Id)));
        Assert.False(await db.Documents.AnyAsync(d => d.Id == oldDoc.Id));
        Assert.True(await db.Documents.AnyAsync(d => d.Id == newDoc.Id));
        var usages = await db.UsageRecords.AsNoTracking().Where(u => u.UserId == user.Id || u.UserId == other.Id).ToListAsync();
        Assert.Equal(2, usages.Count);
        Assert.Null(usages.Single(u => u.UserId == user.Id).DocumentId);
        Assert.Equal(newDoc.Id, usages.Single(u => u.UserId == other.Id).DocumentId);
    }
}
