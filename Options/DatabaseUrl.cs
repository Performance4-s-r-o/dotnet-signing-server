using Npgsql;

namespace DotNetSigningServer.Options;

/// <summary>
/// The database as one URL, the same shape the portal uses
/// (<c>postgresql://user:password@host:5432/postgres?sslmode=require</c>). One Coolify variable
/// per application instead of five <c>DB_*</c> ones, and moving the engine to its own database
/// later means changing that one value.
///
/// <c>DATABASE_URL</c> wins over <c>ConnectionStrings:DefaultConnection</c> / <c>DB_*</c>; empty or
/// an unresolved Coolify placeholder (<see cref="EnvValues.IsUnset"/>) falls back to them, so an
/// installation configured with <c>DB_*</c> keeps working unchanged. The schema is not part of the
/// URL: the engine always pins <c>search_path</c> to <c>dotnet_signing</c> itself.
/// </summary>
public static class DatabaseUrl
{
    public const string Variable = "DATABASE_URL";

    /// <summary>Read only by the one-shot <c>migrate</c> step, never by the serving process.</summary>
    public const string MigrationVariable = "MIGRATION_DATABASE_URL";

    public const int DefaultMaxPoolSize = 20;
    public const int DefaultCommandTimeoutSeconds = 15;
    private const int ConnectTimeoutSeconds = 15;

    /// <summary>
    /// The runtime connection string: <c>DATABASE_URL</c> with the pool and timeouts from
    /// <c>DB_MAX_POOL_SIZE</c> / <c>DB_COMMAND_TIMEOUT_SECONDS</c> (#75), otherwise
    /// <c>ConnectionStrings:DefaultConnection</c>, otherwise the <c>DB_*</c> parts.
    /// </summary>
    public static string? ResolveRuntime(IConfiguration configuration, Func<IConfiguration, string?> fromParts)
    {
        var url = configuration[Variable];
        if (EnvValues.IsUnset(url))
        {
            return configuration.GetConnectionString("DefaultConnection") ?? fromParts(configuration);
        }

        var builder = Parse(url!, Variable);
        builder.MaxPoolSize = EnvValues.Int(configuration, "DB_MAX_POOL_SIZE") ?? DefaultMaxPoolSize;
        builder.Timeout = ConnectTimeoutSeconds;
        builder.CommandTimeout = EnvValues.Int(configuration, "DB_COMMAND_TIMEOUT_SECONDS") ?? DefaultCommandTimeoutSeconds;
        return builder.ConnectionString;
    }

    /// <summary><c>MIGRATION_DATABASE_URL</c> as a connection string, or null when it is not set.</summary>
    public static string? ResolveMigration(IConfiguration configuration)
    {
        var url = configuration[MigrationVariable];
        return EnvValues.IsUnset(url) ? null : Parse(url!, MigrationVariable).ConnectionString;
    }

    /// <summary>
    /// <c>postgres://</c> or <c>postgresql://</c>; user and password percent-decoded, port 5432 and
    /// database <c>postgres</c> when missing. <c>sslmode</c> is honoured, <c>pgbouncer=true</c> turns off
    /// prepared statements and the session reset; other query parameters are ignored. A raw <c>@</c> or
    /// <c>:</c> in the password works; <c>/ ? #</c> must be percent-encoded, as in any URL. Errors
    /// never echo the value: it carries the password.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Parse(string url, string variable)
    {
        // User info ends at the LAST '@' (as libpq reads it), so a raw '@' in the password still
        // works; System.Uri would split at the first one. The rest is parsed without it.
        var trimmed = url.Trim();
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        var at = schemeEnd < 0 ? -1 : trimmed.LastIndexOf('@');
        var userInfo = at > schemeEnd ? trimmed[(schemeEnd + 3)..at] : string.Empty;
        var withoutUser = at > schemeEnd ? trimmed[..(schemeEnd + 3)] + trimmed[(at + 1)..] : trimmed;

        if (!Uri.TryCreate(withoutUser, UriKind.Absolute, out var uri)
            || (uri.Scheme != "postgres" && uri.Scheme != "postgresql")
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                $"{variable} must look like postgresql://user:password@host:5432/database (percent-encode / ? # in the password).");
        }

        var colon = userInfo.IndexOf(':');
        var user = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
        var password = colon < 0 ? null : Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        if (string.IsNullOrEmpty(user))
        {
            throw new InvalidOperationException($"{variable} has no user name.");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host.Trim('[', ']'),
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Username = user,
        };
        if (password != null)
        {
            builder.Password = password;
        }

        var sslMode = QueryValue(uri.Query, "sslmode");
        if (sslMode != null)
        {
            builder.SslMode = sslMode.ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException(
                    $"{variable} has an unknown sslmode '{sslMode}' (disable, allow, prefer, require, verify-ca, verify-full)."),
            };
        }

        // Transaction pooler (Supabase :6543, pgbouncer): no server-side prepared statements and no
        // session reset, the same flags docs/operations.md lists for DB_* on that port.
        if (string.Equals(QueryValue(uri.Query, "pgbouncer"), "true", StringComparison.OrdinalIgnoreCase))
        {
            builder.NoResetOnClose = true;
            builder.MaxAutoPrepare = 0;
        }

        return builder;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
