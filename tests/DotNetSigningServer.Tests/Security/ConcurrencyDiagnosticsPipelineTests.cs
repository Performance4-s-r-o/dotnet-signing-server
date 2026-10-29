using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #69: /internal/concurrency through the real pipeline — internal listener only — and the
/// middleware takes its global pool from the same <see cref="CapacityPools"/> the endpoint shows.
/// </summary>
public sealed class ConcurrencyPipelineFixture : IAsyncLifetime
{
    public const int InternalPort = 8086;
    public const string LocalPortHeader = "X-Test-Local-Port";

    public OutboxPostgresFixture Postgres { get; } = new();
    internal WebApplicationFactory<Program> WithInternalPort { get; private set; } = null!;
    internal WebApplicationFactory<Program> WithoutInternalPort { get; private set; } = null!;
    public string ApiToken { get; private set; } = "";

    /// <summary>TestServer has no sockets; the test says which listener the request came in on.</summary>
    private sealed class LocalPortFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, call) =>
            {
                if (int.TryParse(context.Request.Headers[LocalPortHeader], out var port))
                {
                    context.Connection.LocalPort = port;
                }
                await call();
            });
            next(app);
        };
    }

    private sealed class NoEmail : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }

    private sealed class Factory(string connectionString, bool internalPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("UseLocalDb", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
            builder.UseSetting("GLOBAL_CONCURRENCY_LIMIT", "5");
            if (internalPort)
            {
                builder.UseSetting("INTERNAL_API_PORT", InternalPort.ToString());
            }
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();
                services.AddTransient<IStartupFilter, LocalPortFromHeader>();
            });
        }
    }

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
        WithInternalPort = new Factory(Postgres.ConnectionString, internalPort: true);
        WithoutInternalPort = new Factory(Postgres.ConnectionString, internalPort: false);
        _ = WithInternalPort.Server;
        _ = WithoutInternalPort.Server;

        await using var db = Postgres.CreateContext();
        var user = new User { Email = $"u{Guid.NewGuid():N}@example.com", EmailVerified = true, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        using var scope = WithInternalPort.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var (plaintext, hash, _) = tokens.IssueToken(user, "integration", null);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = "integration",
            TokenHash = hash,
            TokenPrefix = plaintext[..8],
            IsBrowserToken = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        ApiToken = plaintext;
    }

    public async Task DisposeAsync()
    {
        if (WithInternalPort != null) await WithInternalPort.DisposeAsync();
        if (WithoutInternalPort != null) await WithoutInternalPort.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

[Trait("Category", "Db")]
public class ConcurrencyDiagnosticsPipelineTests : IClassFixture<ConcurrencyPipelineFixture>
{
    private readonly ConcurrencyPipelineFixture _fx;

    public ConcurrencyDiagnosticsPipelineTests(ConcurrencyPipelineFixture fx) => _fx = fx;

    private static HttpRequestMessage Get(string path, int? localPort)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (localPort is int port) request.Headers.Add(ConcurrencyPipelineFixture.LocalPortHeader, port.ToString());
        return request;
    }

    [DockerFact]
    public async Task Diagnostics_answer_on_the_internal_listener_only()
    {
        var client = _fx.WithInternalPort.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Get(ConcurrencyDiagnostics.Path, 8085))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Get(ConcurrencyDiagnostics.Path, ConcurrencyPipelineFixture.InternalPort))).StatusCode);
    }

    [DockerFact]
    public async Task Diagnostics_do_not_exist_without_an_internal_listener()
    {
        var client = _fx.WithoutInternalPort.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Get(ConcurrencyDiagnostics.Path, ConcurrencyPipelineFixture.InternalPort))).StatusCode);
    }

    [DockerFact]
    public async Task Middleware_and_diagnostics_share_one_global_pool()
    {
        var pools = _fx.WithInternalPort.Services.GetRequiredService<CapacityPools>();
        Assert.Equal(5, pools.Global.Limit);
        var before = pools.Global.Snapshot().Acquired;

        var client = _fx.WithInternalPort.CreateClient();
        var api = Get("/api/pdf-template", 8085);
        api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fx.ApiToken);
        var response = await client.SendAsync(api);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The slot the middleware took is counted in the pool the endpoint reports.
        Assert.True(pools.Global.Snapshot().Acquired > before);
        var diagnostics = await client.SendAsync(Get(ConcurrencyDiagnostics.Path, ConcurrencyPipelineFixture.InternalPort));
        using var json = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync());
        var global = json.RootElement.GetProperty("pools").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "global");
        Assert.True(global.GetProperty("acquired").GetInt64() > before);
        Assert.Equal(5, global.GetProperty("limit").GetInt32());
    }
}
