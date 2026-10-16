using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DotNetSigningServer.Tests.Data;

/// <summary>
/// The hand-written migrations keep the model snapshot in step with the model. Needs no
/// database: the Npgsql provider builds the model, and the comparison runs in memory.
/// </summary>
public class MigrationSnapshotTests
{
    [Fact]
    public void Model_HasNoChangesMissingFromTheMigrations()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var db = new ApplicationDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Migration79_CreatesGrantsAndKeepsOldGrantKeys()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var db = new ApplicationDbContext(options);

        var sql = db.GetService<IMigrator>().GenerateScript("20261016065736_PortalTenantAndRequestId", "20261016120000_BackofficeReliability79");

        Assert.Contains("CREATE TABLE dotnet_signing.\"CreditGrants\"", sql);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_CreditGrants_GrantKey\"", sql);
        Assert.Contains("WHERE \"PaymentIntentId\" IS NOT NULL", sql);
        Assert.Contains("FROM dotnet_signing.\"WebhookEvents\" w", sql);
        Assert.Contains("ADD \"DeadAt\" timestamp with time zone", sql);
    }
}
