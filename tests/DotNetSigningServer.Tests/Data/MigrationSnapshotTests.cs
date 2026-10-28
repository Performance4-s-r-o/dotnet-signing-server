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

    /// <summary>
    /// The baseline carries what the model generator cannot see; losing any of it would only
    /// show in a schema diff, never in a failing query.
    /// </summary>
    [Fact]
    public void Baseline_CarriesTheHandWrittenObjects()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var db = new ApplicationDbContext(options);

        var migrations = db.Database.GetMigrations().ToList();
        Assert.Single(migrations);
        var sql = db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase, migrations[0]);

        Assert.Contains("CREATE UNIQUE INDEX \"IX_Users_Email_Lower\" ON dotnet_signing.\"Users\" (lower(\"Email\"))", sql);
        Assert.Contains("CREATE FUNCTION dotnet_signing.consent_records_append_only()", sql);
        Assert.Contains("current_setting('dotnet_signing.erasure', true) = 'on'", sql);
        Assert.Contains("CREATE TRIGGER consent_records_append_only", sql);
        Assert.Contains("CREATE TRIGGER consent_records_no_truncate", sql);
        Assert.Contains("CREATE FUNCTION dotnet_signing.admin_audit_append_only()", sql);
        Assert.Contains("CREATE TRIGGER \"AdminAuditEntries_append_only\"", sql);
        Assert.Contains("CREATE TRIGGER \"AdminAuditEntries_no_truncate\"", sql);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_CreditGrants_GrantKey\"", sql);
        Assert.Contains("WHERE \"PaymentIntentId\" IS NOT NULL", sql);
        Assert.Contains("\"PasswordIterations\" integer NOT NULL DEFAULT 0", sql);
    }
}
