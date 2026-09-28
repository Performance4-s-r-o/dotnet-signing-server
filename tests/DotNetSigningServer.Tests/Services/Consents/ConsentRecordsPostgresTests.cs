using DotNetSigningServer.Models;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>The append-only trigger of <c>ConsentRecords</c> on a real PostgreSQL with every migration applied.</summary>
[Trait("Category", "Db")]
public class ConsentRecordsPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public ConsentRecordsPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private async Task<ConsentRecord> InsertAsync()
    {
        var record = new ConsentRecord
        {
            UserId = Guid.NewGuid(),
            Document = "terms",
            Purpose = "terms",
            Version = 1,
            Locale = "en",
            ContentHash = new string('b', 64),
            Action = ConsentActions.Granted,
            Source = ConsentSources.Signup,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        record.SubjectRef = $"dotnet:user:{record.UserId}";
        await using var db = _pg.CreateContext();
        db.ConsentRecords.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    [DockerFact]
    public async Task Insert_Works()
    {
        var record = await InsertAsync();

        await using var db = _pg.CreateContext();
        var stored = await db.ConsentRecords.AsNoTracking().SingleAsync(r => r.Id == record.Id);
        Assert.Equal(new string('b', 64), stored.ContentHash);
    }

    [DockerFact]
    public async Task Update_IsRefused()
    {
        var record = await InsertAsync();

        await using var db = _pg.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ConsentRecords.Where(r => r.Id == record.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, 2)));
        Assert.Contains("append-only", ex.MessageText);

        var tracked = await db.ConsentRecords.SingleAsync(r => r.Id == record.Id);
        tracked.OutboxItemId = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DockerFact]
    public async Task Delete_IsRefused()
    {
        var record = await InsertAsync();

        await using var db = _pg.CreateContext();
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.ConsentRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("TRUNCATE dotnet_signing.\"ConsentRecords\""));

        Assert.True(await db.ConsentRecords.AnyAsync(r => r.Id == record.Id));
    }
}
