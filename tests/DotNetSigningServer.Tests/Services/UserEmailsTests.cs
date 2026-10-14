using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#82: an e-mail address names one account whatever its letter case.</summary>
public class UserEmailsTests
{
    [Theory]
    [InlineData("Foo@Example.CZ", "Foo@Example.CZ", "foo@example.cz")]
    [InlineData("  foo@example.cz ", "foo@example.cz", "foo@example.cz")]
    [InlineData(null, "", "")]
    public void Trim_KeepsCase_KeyFoldsIt(string? input, string trimmed, string key)
    {
        Assert.Equal(trimmed, UserEmails.Trim(input));
        Assert.Equal(key, UserEmails.Key(input));
    }

    [Theory]
    [InlineData("foo@example.cz", "FOO@Example.cz")]
    [InlineData("Foo@Example.cz", "foo@example.cz")] // account created before normalisation
    [InlineData("foo@example.cz", " foo@example.cz ")]
    public async Task FindByEmail_IgnoresCase(string stored, string typed)
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        var user = TestHelpers.CreateTestUser(stored);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(user.Id, (await db.Users.FindByEmailAsync(typed))?.Id);
        Assert.True(await db.Users.AnyWithEmailAsync(typed));
        Assert.Null(await db.Users.FindByEmailAsync("other@example.cz"));
        Assert.Null(await db.Users.FindByEmailAsync(""));
    }

    [Fact]
    public async Task FindByEmail_TwoLegacyAccountsDifferingByCase_ReturnsOnlyTheExactOne()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        var upper = TestHelpers.CreateTestUser("Foo@example.cz");
        var lower = TestHelpers.CreateTestUser("foo@example.cz");
        db.Users.AddRange(upper, lower);
        await db.SaveChangesAsync();

        Assert.Equal(upper.Id, (await db.Users.FindByEmailAsync("Foo@example.cz"))?.Id);
        Assert.Equal(lower.Id, (await db.Users.FindByEmailAsync("foo@example.cz"))?.Id);
        Assert.Null(await db.Users.FindByEmailAsync("FOO@example.cz")); // ambiguous: no guess
    }
}

/// <summary>The unique index on <c>lower("Email")</c> (migration AddUserEmailLowerUniqueIndex).</summary>
[Trait("Category", "Db")]
public class UserEmailsPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public UserEmailsPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public async Task SecondAccount_DifferingOnlyByCase_IsRefusedByTheDatabase()
    {
        var local = Guid.NewGuid().ToString("N");
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(TestHelpers.CreateTestUser($"{local}@example.cz"));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(TestHelpers.CreateTestUser($"{local.ToUpperInvariant()}@Example.CZ"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = _pg.CreateContext())
        {
            var found = await db.Users.FindByEmailAsync($"{local.ToUpperInvariant()}@EXAMPLE.cz");
            Assert.NotNull(found);
            Assert.Equal($"{local}@example.cz", found.Email);
        }
    }

    [Fact]
    public async Task Migration_WithExistingAccountsDifferingOnlyByCase_StopsInsteadOfGuessing()
    {
        await using var db = _pg.CreateContext();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("AddApiOperations");
        var local = Guid.NewGuid().ToString("N");
        var lower = TestHelpers.CreateTestUser($"{local}@example.cz");
        db.Users.AddRange(lower, TestHelpers.CreateTestUser($"{local.ToUpperInvariant()}@example.cz"));
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync());
        Assert.Contains("differing only by letter case", ex.MessageText);

        // Resolved by a person (here: the second account removed), the migration goes through.
        await db.Users.Where(u => u.Email != lower.Email && u.Email.ToLower() == lower.Email).ExecuteDeleteAsync();
        await migrator.MigrateAsync();
    }

    /// <summary>
    /// A database whose <c>LC_CTYPE</c> is C folds only ASCII in <c>lower()</c>. Folding the
    /// typed address in .NET instead (ToLowerInvariant) would then never match a stored
    /// non-ASCII address, not even typed exactly as stored.
    /// </summary>
    [Fact]
    public async Task NonAsciiAddress_IsFound_InADatabaseWithCLocale()
    {
        var name = $"c_locale_{Guid.NewGuid():N}";
        await using (var admin = new Npgsql.NpgsqlConnection(_pg.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name} TEMPLATE template0 LC_COLLATE 'C' LC_CTYPE 'C' ENCODING 'UTF8'";
            await create.ExecuteNonQueryAsync();
        }
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(_pg.ConnectionString) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<DotNetSigningServer.Data.ApplicationDbContext>()
            .UseNpgsql(connection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dotnet_signing"))
            .Options;

        await using var db = new DotNetSigningServer.Data.ApplicationDbContext(options);
        await db.Database.MigrateAsync();
        var user = TestHelpers.CreateTestUser("Žaneta.Nováková@Example.cz");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(user.Id, (await db.Users.FindByEmailAsync("Žaneta.Nováková@Example.cz"))?.Id);
        Assert.Equal(user.Id, (await db.Users.FindByEmailAsync(" Žaneta.Nováková@EXAMPLE.cz"))?.Id);
        Assert.True(await db.Users.AnyWithEmailAsync("Žaneta.Nováková@example.CZ"));
    }
}
