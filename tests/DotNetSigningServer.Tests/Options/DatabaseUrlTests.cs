using DotNetSigningServer.Options;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DotNetSigningServer.Tests.Options;

/// <summary>DATABASE_URL: one URL like the portal's, falling back to DefaultConnection / DB_*.</summary>
public class DatabaseUrlTests
{
    private const string Fallback = "Host=parts;Port=5432;Database=signing;Username=u;Password=p";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static NpgsqlConnectionStringBuilder Runtime(IConfiguration config) =>
        new(DatabaseUrl.ResolveRuntime(config, _ => Fallback)!);

    [Fact]
    public void SupabasePoolerUrl_Parses()
    {
        var b = DatabaseUrl.Parse(
            "postgresql://dotnet_signing.czjndhwnmfqwblcsoppa:p%40ss%3Aw%2Frd@aws-0-eu-west-1.pooler.supabase.com:5432/postgres?sslmode=require&pgbouncer=true",
            "DATABASE_URL");

        Assert.Equal("aws-0-eu-west-1.pooler.supabase.com", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("postgres", b.Database);
        Assert.Equal("dotnet_signing.czjndhwnmfqwblcsoppa", b.Username);
        Assert.Equal("p@ss:w/rd", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void MissingPortAndDatabase_Default()
    {
        var b = DatabaseUrl.Parse("postgres://u:p@db", "DATABASE_URL");
        Assert.Equal(5432, b.Port);
        Assert.Equal("postgres", b.Database);
    }

    [Theory]
    [InlineData("mysql://u:p@db/x")]
    [InlineData("not a url")]
    [InlineData("postgresql://@db/x")]
    [InlineData("postgresql://u:p@db/x?sslmode=sometimes")]
    public void Garbage_FailsWithoutEchoingThePassword(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseUrl.Parse(url, "DATABASE_URL"));
        Assert.DoesNotContain(":p@", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{{project.DOTNET_DATABASE_URL}}")]
    [InlineData("{{environment.DOTNET_DATABASE_URL}}")]
    public void Unset_FallsBackToDefaultConnectionOrParts(string? url)
    {
        Assert.Equal("parts", Runtime(Config(("DATABASE_URL", url))).Host);
        Assert.Equal("dc", Runtime(Config(("DATABASE_URL", url), ("ConnectionStrings:DefaultConnection", "Host=dc;Username=u"))).Host);
        Assert.Null(DatabaseUrl.ResolveMigration(Config(("MIGRATION_DATABASE_URL", url))));
    }

    [Fact]
    public void Url_WinsOverDefaultConnection_AndKeepsPoolSettings()
    {
        var b = Runtime(Config(
            ("DATABASE_URL", "postgresql://u:p@url-host:6000/db"),
            ("ConnectionStrings:DefaultConnection", "Host=;Port=;Database=;Username=;Password=;Maximum Pool Size=20"),
            ("DB_MAX_POOL_SIZE", "7"),
            ("DB_COMMAND_TIMEOUT_SECONDS", "{{project.X}}")));

        Assert.Equal("url-host", b.Host);
        Assert.Equal(6000, b.Port);
        Assert.Equal(7, b.MaxPoolSize);
        Assert.Equal(DatabaseUrl.DefaultCommandTimeoutSeconds, b.CommandTimeout);
        Assert.Equal(15, b.Timeout);
    }

    [Fact]
    public void MigrationUrl_Parses()
    {
        var cs = DatabaseUrl.ResolveMigration(Config(("MIGRATION_DATABASE_URL", "postgresql://owner:secret@db:5432/postgres")));
        Assert.Equal("owner", new NpgsqlConnectionStringBuilder(cs!).Username);
    }
}
