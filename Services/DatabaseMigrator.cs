using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Schema migrations as a release step rather than a side effect of every start (#85).
/// EF Core 8 has no migration lock, so two replicas starting together could migrate at the
/// same time, and the runtime role would need DDL rights.
/// </summary>
public static class DatabaseMigrator
{
    public const string Schema = "dotnet_signing";

    /// <summary>The first migration of the history; every database built since carries it.</summary>
    public const string BaselineMigrationId = "20261028234306_InitialSchema";

    /// <summary>Command-line switch: migrate and exit, never start serving.</summary>
    public const string MigrateArgument = "--migrate";

    /// <summary>
    /// The runtime connection with the migration role's credentials swapped in. Empty user =
    /// no separate role, the runtime connection is used as it is. A user without a password
    /// is refused rather than silently paired with the runtime password.
    /// </summary>
    public static string BuildMigrationConnectionString(string runtimeConnectionString, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return runtimeConnectionString;
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Database:MigrationUser is set but Database:MigrationPassword is not. Set MIGRATION_DB_PASSWORD.");
        }

        return new Npgsql.NpgsqlConnectionStringBuilder(runtimeConnectionString)
        {
            Username = user,
            Password = password,
        }.ConnectionString;
    }

    /// <summary>
    /// Context for the migration role. A migration can take longer than a request, so the
    /// command timeout is generous; there is no retry strategy, a failed migration must stop.
    /// </summary>
    public static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.CommandTimeout(600);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);
            })
            .Options;
        return new ApplicationDbContext(options);
    }

    /// <summary>Creates the schema if the role may and applies every pending migration.</summary>
    public static void EnsureSchemaAndMigrate(ApplicationDbContext dbContext, ILogger logger)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            CreateSchemaIfAllowed(dbContext, logger);
            RequireCurrentHistory(dbContext, logger);
            dbContext.Database.Migrate();
            return;
        }

        // EF Core 8 takes no migration lock, so two concurrent steps would race on the same
        // DDL, CREATE SCHEMA included. A session advisory lock on the connection EF itself
        // uses serialises them; the second one waits, then finds nothing pending.
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
        }

        Execute(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})");
        try
        {
            CreateSchemaIfAllowed(dbContext, logger);
            RequireCurrentHistory(dbContext, logger);
            dbContext.Database.Migrate();
        }
        finally
        {
            Execute(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})");
        }
    }

    private static void CreateSchemaIfAllowed(ApplicationDbContext dbContext, ILogger logger)
    {
        // Ensure the target schema exists before EF Core looks up __EFMigrationsHistory.
        // Npgsql's SET search_path at connection open silently ignores missing schemas,
        // so on a fresh database the history lookup would fail with 42P01.
        //
        // CREATE SCHEMA IF NOT EXISTS still needs CREATE on the database even when
        // the schema already exists. Managed database environments often run
        // the app as a least-privilege role that lacks DB-level CREATE — in which
        // case operators are expected to pre-create the schema with the right
        // ownership. Probe first, then attempt CREATE; tolerate 42501 if the
        // schema is already there.
        if (!dbContext.Database.IsNpgsql())
        {
            return;
        }

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
        }

        using var probe = conn.CreateCommand();
        probe.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'dotnet_signing')";
        var schemaExists = (bool)(probe.ExecuteScalar() ?? false);

        if (schemaExists)
        {
            return;
        }

        try
        {
            dbContext.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS dotnet_signing");
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42501")
        {
            logger.LogCritical(
                "Schema 'dotnet_signing' does not exist and the DB role lacks CREATE on database '{Database}'. " +
                "Pre-create the schema manually (e.g. CREATE SCHEMA dotnet_signing AUTHORIZATION <role>) or " +
                "switch the connection to a dedicated database where the role has CREATE.",
                conn.Database);
            throw;
        }
    }

    // Arbitrary constant: "P4PD" + "MIGR" as ASCII, one lock per database.
    private const long AdvisoryLockKey = 0x50345044_4D494752;

    private static void Execute(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Tables created by the migration role belong to it, so the runtime role has to be given
    /// access explicitly. Run after every migration step: it covers what the step just
    /// created, which is why no default privileges are needed. A no-op when both are one role.
    /// </summary>
    public static void GrantRuntimeAccess(ApplicationDbContext dbContext, string? runtimeRole, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(runtimeRole) || !dbContext.Database.IsNpgsql())
        {
            return;
        }

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
        }

        using (var who = conn.CreateCommand())
        {
            who.CommandText = "SELECT current_user";
            if (string.Equals((string?)who.ExecuteScalar(), runtimeRole, StringComparison.Ordinal))
            {
                return;
            }
        }

        var role = new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(runtimeRole);
        Execute(conn, $"GRANT USAGE ON SCHEMA {Schema} TO {role}");
        Execute(conn, $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {Schema} TO {role}");
        Execute(conn, $"GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {Schema} TO {role}");
        logger.LogInformation("Granted the runtime role access to schema {Schema}.", Schema);
    }

    /// <summary>
    /// Refuses to start against a schema that is behind the code, with a message that says
    /// what to run. Serving against a half-migrated database silently breaks queries.
    /// </summary>
    public static void RequireNoPendingMigrations(ApplicationDbContext dbContext, ILogger logger)
    {
        RequireCurrentHistory(dbContext, logger);

        var pending = dbContext.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", pending);
        logger.LogCritical(
            "{Count} database migration(s) are pending: {Names}. The server does not migrate on start. " +
            "Run 'dotnet dotnet-signing-server.dll {Switch}' once as a release step with the migration role, " +
            "or set RUN_MIGRATIONS_ON_START=true (single-instance / on-prem installs only).",
            pending.Count, names, MigrateArgument);
        throw new InvalidOperationException(
            $"{pending.Count} database migration(s) are pending ({names}). Run the {MigrateArgument} step before starting the server.");
    }

    /// <summary>
    /// The history was squashed into one baseline migration before the first release. A database
    /// that still carries the old history would see the baseline as pending and fail half-way
    /// through its CREATE TABLE, so it is refused up front, with what to do instead.
    /// </summary>
    public static void RequireCurrentHistory(ApplicationDbContext dbContext, ILogger logger)
    {
        var applied = dbContext.Database.GetAppliedMigrations().ToList();
        if (applied.Count == 0)
        {
            return;
        }

        const string baseline = BaselineMigrationId;
        if (applied.Contains(baseline, StringComparer.Ordinal))
        {
            return;
        }

        logger.LogCritical(
            "Schema {Schema} holds a migration history from before the baseline {Baseline} ({Count} entries, first {First}). " +
            "It cannot be upgraded in place. Back up what you need, drop the schema (DROP SCHEMA {Schema} CASCADE) " +
            "and run the {Switch} step against the empty database.",
            Schema, baseline, applied.Count, applied[0], Schema, MigrateArgument);
        throw new InvalidOperationException(
            $"Schema {Schema} holds a migration history from before the baseline {baseline} ({applied.Count} entries, first {applied[0]}). " +
            $"It cannot be upgraded in place: drop schema {Schema} and run the {MigrateArgument} step against the empty database.");
    }
}
