using System.Net;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

/// <summary>Runs only where Docker is reachable; set SKIP_DB_TESTS=1 to skip explicitly.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailable()) Skip = "Docker is not available (or SKIP_DB_TESTS is set)";
    }

    private static bool DockerAvailable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKIP_DB_TESTS"))) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))) return true;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists("/var/run/docker.sock")
               || File.Exists(Path.Combine(home, ".docker", "run", "docker.sock"))
               || OperatingSystem.IsWindows();
    }
}

/// <summary>Throwaway PostgreSQL with every migration applied, schema as in production.</summary>
public sealed class OutboxPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            SearchPath = "dotnet_signing",
        }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public void Configure(DbContextOptionsBuilder options) =>
        options.UseNpgsql(ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dotnet_signing"));

    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        Configure(options);
        return new ApplicationDbContext(options.Options);
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

[Trait("Category", "Db")]
public class OutboxPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public OutboxPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private static readonly OutboxPayloadProtector Protector = new(new EphemeralDataProtectionProvider());

    private BackofficeOutbox Outbox(ApplicationDbContext db, OutboxSignal signal) =>
        new(db, Protector, signal, TimeProvider.System);

    private async Task ClearAsync()
    {
        await using var db = _pg.CreateContext();
        await db.BackofficeOutboxItems.ExecuteDeleteAsync();
    }

    [DockerFact]
    public async Task Migration_CreatesTheTableAndIndexes()
    {
        await using var db = _pg.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select indexname from pg_indexes where schemaname = 'dotnet_signing' and tablename = 'BackofficeOutboxItems' order by 1", conn);
        var indexes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));

        Assert.Equal(
            new[] { "IX_BackofficeOutboxItems_CreatedAt", "IX_BackofficeOutboxItems_Status_NextAttemptAt", "PK_BackofficeOutboxItems" },
            indexes);
    }

    [DockerFact]
    public async Task RolledBackTransaction_LeavesNoRowAndNoSignal()
    {
        await ClearAsync();
        var signal = new OutboxSignal();
        await using (var db = _pg.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.WebhookEvents.Add(new WebhookEvent { EventId = "evt_" + Guid.NewGuid(), PayloadJson = "{}" });
            Outbox(db, signal).Enqueue("test", new { n = 1 });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Empty(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        await using var check = _pg.CreateContext();
        Assert.Equal(0, await check.BackofficeOutboxItems.CountAsync());
    }

    [DockerFact]
    public async Task ExplicitTransaction_SignalsOnlyAfterCommit()
    {
        await ClearAsync();
        var signal = new OutboxSignal();
        await using var db = _pg.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var id = Outbox(db, signal).Enqueue("test", new { n = 1 });
        await db.SaveChangesAsync();

        Assert.Empty(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        await tx.CommitAsync();

        Assert.Equal(new[] { id }, await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [DockerFact]
    public async Task Claim_SkipsRowsLockedByAnotherInstance()
    {
        await ClearAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var seed = _pg.CreateContext())
        {
            var outbox = Outbox(seed, new OutboxSignal());
            for (var i = 0; i < 30; i++) outbox.Enqueue("test", new { n = i });
            await seed.SaveChangesAsync();
        }

        // Instance A claims inside a transaction it keeps open: its rows stay row-locked.
        await using var a = _pg.CreateContext();
        await using var txA = await a.Database.BeginTransactionAsync();
        var claimedA = await OutboxClaim.ClaimDueAsync(a, now.AddSeconds(1), CancellationToken.None);

        // Instance B claims at the same time: it neither waits nor gets A's rows.
        await using var b = _pg.CreateContext();
        var claimB = OutboxClaim.ClaimDueAsync(b, now.AddSeconds(1), CancellationToken.None);
        var finished = await Task.WhenAny(claimB, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(claimB, finished);
        var claimedB = await claimB;

        Assert.Equal(OutboxClaim.BatchSize, claimedA.Count);
        Assert.Equal(30 - OutboxClaim.BatchSize, claimedB.Count);
        Assert.Empty(claimedA.Select(i => i.Id).Intersect(claimedB.Select(i => i.Id)));
        await txA.CommitAsync();
    }

    [DockerFact]
    public async Task ConcurrentClaims_NeverReturnTheSameItemTwice()
    {
        await ClearAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var seed = _pg.CreateContext())
        {
            var outbox = Outbox(seed, new OutboxSignal());
            for (var i = 0; i < 50; i++) outbox.Enqueue("test", new { n = i });
            await seed.SaveChangesAsync();
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = _pg.CreateContext();
            var items = await OutboxClaim.ClaimDueAsync(db, now.AddSeconds(1), CancellationToken.None);
            return items.Select(i => i.Id).ToList();
        }));

        // A claim racing others may come back short (rows re-checked after a concurrent
        // commit drop out of its LIMIT); whatever is left is claimed afterwards.
        var all = claims.SelectMany(c => c).ToList();
        await using (var rest = _pg.CreateContext())
        {
            List<BackofficeOutboxItem> more;
            while ((more = await OutboxClaim.ClaimDueAsync(rest, now.AddSeconds(1), CancellationToken.None)).Count > 0)
                all.AddRange(more.Select(i => i.Id));
        }

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(50, all.Count);
    }

    [DockerFact]
    public async Task Processor_SendsAndClearsThePayload()
    {
        await ClearAsync();
        using var host = new OutboxTestHost(_pg.Configure);
        host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"em_1"}""");
        var id = await host.EnqueueAsync(new { code = "493817" });

        await using (var raw = _pg.CreateContext())
        {
            var stored = await raw.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == id);
            Assert.DoesNotContain("493817", stored.PayloadProtected);
        }

        Assert.Equal(1, await host.Processor.DispatchDueAsync(CancellationToken.None));

        await using var check = _pg.CreateContext();
        var item = await check.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("em_1", item.RemoteId);
        Assert.Null(item.PayloadProtected);
        Assert.Null(item.LockedUntil);
        Assert.Equal(0, await OutboxHealth.CleanupAsync(check, DateTimeOffset.UtcNow));
        Assert.Equal(1, await OutboxHealth.CleanupAsync(check, DateTimeOffset.UtcNow.AddDays(31)));
    }
}
