using DotNetSigningServer.Data;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DotNetSigningServer.Tests.Services.Database;

/// <summary>One PostgreSQL for the class; every test gets its own EMPTY database on it.</summary>
public sealed class EmptyDatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
    }

    public string AdminConnectionString => _container!.GetConnectionString();

    public async Task<string> NewDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = name,
            SearchPath = DatabaseMigrator.Schema,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

internal sealed class StartupFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string? _runMigrationsOnStart;

    public StartupFactory(string connectionString, string? runMigrationsOnStart)
    {
        _connectionString = connectionString;
        _runMigrationsOnStart = runMigrationsOnStart;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("UseLocalDb", "false");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
        if (_runMigrationsOnStart != null)
        {
            builder.UseSetting("Database:RunMigrationsOnStart", _runMigrationsOnStart);
        }

        builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
    }
}

[Trait("Category", "Db")]
public class DatabaseMigratorTests : IClassFixture<EmptyDatabaseFixture>
{
    private readonly EmptyDatabaseFixture _fixture;

    public DatabaseMigratorTests(EmptyDatabaseFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task Pending_migrations_are_refused_with_a_message_that_names_the_step()
    {
        await using var db = DatabaseMigrator.CreateContext(await _fixture.NewDatabaseAsync());

        var ex = Assert.Throws<InvalidOperationException>(
            () => DatabaseMigrator.RequireNoPendingMigrations(db, NullLogger.Instance));

        Assert.Contains(DatabaseMigrator.MigrateArgument, ex.Message);
    }

    [DockerFact]
    public async Task After_the_migrate_step_nothing_is_pending()
    {
        await using var db = DatabaseMigrator.CreateContext(await _fixture.NewDatabaseAsync());

        DatabaseMigrator.EnsureSchemaAndMigrate(db, NullLogger.Instance);

        DatabaseMigrator.RequireNoPendingMigrations(db, NullLogger.Instance);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [DockerFact]
    public async Task Start_without_the_switch_does_not_migrate_and_refuses_to_run()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        using var factory = new StartupFactory(connectionString, runMigrationsOnStart: null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(DatabaseMigrator.MigrateArgument, ex.ToString());

        // Nothing was created behind the refusal.
        await using var db = DatabaseMigrator.CreateContext(connectionString);
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
    }

    [DockerFact]
    public async Task Start_with_the_switch_still_migrates()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        using var factory = new StartupFactory(connectionString, runMigrationsOnStart: "true");

        using var client = factory.CreateClient();

        await using var db = DatabaseMigrator.CreateContext(connectionString);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [DockerFact]
    public async Task Start_against_a_migrated_database_needs_no_switch()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        await using (var db = DatabaseMigrator.CreateContext(connectionString))
        {
            DatabaseMigrator.EnsureSchemaAndMigrate(db, NullLogger.Instance);
        }

        using var factory = new StartupFactory(connectionString, runMigrationsOnStart: null);
        using var client = factory.CreateClient();
    }

    [DockerFact]
    public async Task History_from_before_the_baseline_is_refused_by_migrate_and_by_start()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE SCHEMA dotnet_signing;
                CREATE TABLE dotnet_signing."__EFMigrationsHistory" (
                    "MigrationId" character varying(150) PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL);
                INSERT INTO dotnet_signing."__EFMigrationsHistory"
                VALUES ('20260419085537_InitialSchema', '9.0.19'), ('20261016123805_AccountErasure217', '9.0.19');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = DatabaseMigrator.CreateContext(connectionString);
        var migrate = Assert.Throws<InvalidOperationException>(
            () => DatabaseMigrator.EnsureSchemaAndMigrate(db, NullLogger.Instance));
        Assert.Contains("before the baseline", migrate.Message);
        Assert.Contains("DROP SCHEMA", migrate.Message, StringComparison.OrdinalIgnoreCase);

        var start = Assert.Throws<InvalidOperationException>(
            () => DatabaseMigrator.RequireNoPendingMigrations(db, NullLogger.Instance));
        Assert.Contains("before the baseline", start.Message);

        using var factory = new StartupFactory(connectionString, runMigrationsOnStart: "true");
        var onStart = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("before the baseline", onStart.ToString());

        // Refused before anything ran: no table was created next to the old history.
        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync();
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'dotnet_signing'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [DockerFact]
    public async Task Baseline_refuses_a_second_account_differing_only_by_letter_case()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        await using (var db = DatabaseMigrator.CreateContext(connectionString))
        {
            DatabaseMigrator.EnsureSchemaAndMigrate(db, NullLogger.Instance);
        }

        await using (var db = DatabaseMigrator.CreateContext(connectionString))
        {
            db.Users.Add(TestHelpers.CreateTestUser("a@x.cz"));
            await db.SaveChangesAsync();
        }

        await using var second = DatabaseMigrator.CreateContext(connectionString);
        second.Users.Add(TestHelpers.CreateTestUser("A@x.cz"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("IX_Users_Email_Lower", pg.ConstraintName);
    }

    [Fact]
    public void Migration_connection_swaps_only_the_credentials()
    {
        var runtime = "Host=db;Port=5432;Database=signing;Username=app;Password=runtime-pw;SearchPath=dotnet_signing";

        var migration = new NpgsqlConnectionStringBuilder(
            DatabaseMigrator.BuildMigrationConnectionString(runtime, "migrator", "migrator-pw"));

        Assert.Equal("migrator", migration.Username);
        Assert.Equal("migrator-pw", migration.Password);
        Assert.Equal("signing", migration.Database);
        Assert.Equal("dotnet_signing", migration.SearchPath);
        Assert.Equal(runtime, DatabaseMigrator.BuildMigrationConnectionString(runtime, "", null));
        Assert.Throws<InvalidOperationException>(
            () => DatabaseMigrator.BuildMigrationConnectionString(runtime, "migrator", ""));
    }

    [DockerFact]
    public async Task Runtime_role_without_ddl_rights_serves_what_the_migration_role_created()
    {
        var connectionString = await _fixture.NewDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var id = Guid.NewGuid().ToString("N")[..8];
        string migrator = "mig_" + id, runtime = "run_" + id;

        await using (var admin = new NpgsqlConnection(_fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            foreach (var sql in new[]
            {
                $"CREATE ROLE {migrator} LOGIN PASSWORD 'm'",
                $"CREATE ROLE {runtime} LOGIN PASSWORD 'r'",
                $"GRANT CREATE ON DATABASE \"{database}\" TO {migrator}",
            })
            {
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        string As(string role, string password) =>
            new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = password }.ConnectionString;

        // Without a separate role the runtime role is blind; with the step it is not.
        await using (var migration = DatabaseMigrator.CreateContext(As(migrator, "m")))
        {
            DatabaseMigrator.EnsureSchemaAndMigrate(migration, NullLogger.Instance);
            DatabaseMigrator.GrantRuntimeAccess(migration, runtime, NullLogger.Instance);
        }

        await using var serving = DatabaseMigrator.CreateContext(As(runtime, "r"));
        DatabaseMigrator.RequireNoPendingMigrations(serving, NullLogger.Instance);
        Assert.Equal(0, await serving.Users.CountAsync());
    }

    [DockerFact]
    public async Task Two_migrate_steps_at_once_both_succeed()
    {
        var connectionString = await _fixture.NewDatabaseAsync();

        var steps = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var db = DatabaseMigrator.CreateContext(connectionString);
            DatabaseMigrator.EnsureSchemaAndMigrate(db, NullLogger.Instance);
        })).ToArray();

        await Task.WhenAll(steps);

        await using var check = DatabaseMigrator.CreateContext(connectionString);
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }
}
