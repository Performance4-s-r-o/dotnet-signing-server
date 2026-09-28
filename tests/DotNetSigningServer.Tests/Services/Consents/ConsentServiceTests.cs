using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentServiceTests
{
    [Fact]
    public async Task Signup_AddsThreeRecordsAndOneOutboxBatch_WithoutSaving()
    {
        using var host = ConsentTestHost.Create("On");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = ConsentTestHost.NewUser();
        db.Users.Add(user);

        var records = scope.ServiceProvider.GetRequiredService<ConsentService>()
            .RecordSignupConsents(user, ConsentTestHost.SignupChoices(), "Mozilla/5.0 (test)", "203.0.113.7");

        // Nothing is written by the service itself: the caller's SaveChanges does it.
        Assert.Equal(0, await host.WithDbAsync(d => d.ConsentRecords.CountAsync()));
        Assert.Equal(0, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync()));
        Assert.Equal(3, db.ChangeTracker.Entries<ConsentRecord>().Count(e => e.State == EntityState.Added));
        Assert.Single(db.ChangeTracker.Entries<BackofficeOutboxItem>(), e => e.State == EntityState.Added);

        await db.SaveChangesAsync();

        Assert.Equal(1, await host.WithDbAsync(d => d.Users.CountAsync()));
        var stored = await host.WithDbAsync(d => d.ConsentRecords.AsNoTracking().OrderBy(r => r.Document).ToListAsync());
        Assert.Equal(["dpa", "privacy", "terms"], stored.Select(r => r.Document));
        var outbox = Assert.Single(await host.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking().ToListAsync()));
        Assert.Equal("consent", outbox.Kind);
        Assert.Equal($"dotnet:user:{user.Id}", outbox.SubjectRef);
        Assert.All(stored, r =>
        {
            Assert.Equal(user.Id, r.UserId);
            Assert.Equal($"dotnet:user:{user.Id}", r.SubjectRef);
            Assert.Equal(r.Document, r.Purpose);
            Assert.Equal(ConsentSources.Signup, r.Source);
            Assert.Equal("web", r.Channel);
            Assert.Equal("Mozilla/5.0 (test)", r.UserAgent);
            Assert.Equal(outbox.Id, r.OutboxItemId);
            Assert.Equal(host.Time.Now, r.OccurredAt);
        });
        Assert.Equal(ConsentActions.Acknowledged, stored.Single(r => r.Document == "privacy").Action);
        Assert.Equal(ConsentActions.Granted, stored.Single(r => r.Document == "terms").Action);
        Assert.Equal(records.Select(r => r.Id).Order(), stored.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task Off_RecordsLocallyOnly()
    {
        using var host = ConsentTestHost.Create("Off");
        await ConsentTestHost.SignUpAsync(host, "off@example.com");

        var stored = await host.WithDbAsync(d => d.ConsentRecords.AsNoTracking().ToListAsync());
        Assert.Equal(3, stored.Count);
        Assert.All(stored, r => Assert.Null(r.OutboxItemId));
        Assert.Equal(0, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync()));
    }

    [Fact]
    public async Task Shadow_SendsToTheServiceToo()
    {
        using var host = ConsentTestHost.Create("Shadow");
        await ConsentTestHost.SignUpAsync(host, "shadow@example.com");

        Assert.Equal(1, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync()));
    }

    [Fact]
    public async Task Payload_IsOneBatchWithThreeEvents_IpOnlyForTheDpa()
    {
        using var host = ConsentTestHost.Create("On");
        var user = await ConsentTestHost.SignUpAsync(host, "payload@example.com");
        var item = Assert.Single(await host.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking().ToListAsync()));
        var json = host.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(item.PayloadProtected!);

        using var doc = JsonDocument.Parse(json);
        var events = doc.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        foreach (var e in events)
        {
            Assert.Equal($"dotnet:user:{user.Id}", e.GetProperty("subject_ref").GetString());
            var document = e.GetProperty("document").GetString();
            Assert.Equal(document == "dpa" ? "203.0.113.7" : null, e.GetProperty("ip").GetString());
        }
    }

    [Fact]
    public async Task LongUserAgent_IsTruncated()
    {
        using var host = ConsentTestHost.Create("On");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = ConsentTestHost.NewUser();
        db.Users.Add(user);

        var records = scope.ServiceProvider.GetRequiredService<ConsentService>()
            .RecordSignupConsents(user, ConsentTestHost.SignupChoices(), new string('x', 2000));

        Assert.All(records, r => Assert.Equal(ConsentService.MaxUserAgentLength, r.UserAgent!.Length));
    }

    [Fact]
    public async Task Reconsent_RecordsSourceAndFlow()
    {
        using var host = ConsentTestHost.Create("On");
        var userId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ConsentService>()
                .RecordReconsent(userId, ConsentTestHost.SignupChoices("cs"), ConsentService.Flows.Initial, "ua");
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync();
        }

        var stored = await host.WithDbAsync(d => d.ConsentRecords.AsNoTracking().ToListAsync());
        Assert.All(stored, r =>
        {
            Assert.Equal(ConsentSources.Reconsent, r.Source);
            Assert.Equal("cs", r.Locale);
        });
        var item = Assert.Single(await host.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking().ToListAsync()));
        var json = host.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(item.PayloadProtected!);
        Assert.Contains("\"flow\":\"initial\"", json);
    }

    [Fact]
    public async Task Backfill_QueuesRecordsMadeWhileOff_Once()
    {
        // Two sign-ups while Off …
        var dbName = "consents-" + Guid.NewGuid();
        using (var off = ConsentTestHost.Create("Off", o => o.UseInMemoryDatabase(dbName)))
        {
            await ConsentTestHost.SignUpAsync(off, "a@example.com");
            off.Time.Advance(TimeSpan.FromMinutes(1));
            await ConsentTestHost.SignUpAsync(off, "b@example.com");
        }

        // … then the module is switched on and the backfill runs twice.
        using var on = ConsentTestHost.Create("On", o => o.UseInMemoryDatabase(dbName));
        on.Time.Advance(TimeSpan.FromMinutes(5));
        var first = await BackfillAsync(on);
        var second = await BackfillAsync(on);

        Assert.Equal(new ConsentBackfillResult(6, 2), first);
        Assert.Equal(new ConsentBackfillResult(0, 0), second);
        var items = await on.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking().ToListAsync());
        Assert.Equal(2, items.Count);
        var json = on.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(items[0].PayloadProtected!);
        Assert.Contains("\"backfill\":true", json);
        // The records themselves are never updated.
        Assert.All(await on.WithDbAsync(d => d.ConsentRecords.AsNoTracking().ToListAsync()), r => Assert.Null(r.OutboxItemId));
    }

    private static async Task<ConsentBackfillResult> BackfillAsync(Backoffice.Outbox.OutboxTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConsentBackfill>().RunAsync();
    }
}
