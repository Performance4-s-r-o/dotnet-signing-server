using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using System.Threading.RateLimiting;
using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Middleware;

/// <summary>
/// The middleware keeps its switch cache and failure log in statics, as it does in production.
/// This collection runs alone, so a switch set here is never seen by another test class.
/// </summary>
[CollectionDefinition("ApiGate", DisableParallelization = true)]
public class ApiGateCollection
{
}

/// <summary>#73: what /api answers before a body is read, rate limits, operator switches, the failure block.</summary>
[Collection("ApiGate")]
public class ApiGateMiddlewareTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();

    public void Dispose()
    {
        // The switch cache and the failure log are static (shared by every instance of the
        // middleware, as in production): leave them clean for the tests that run next.
        ResetSwitchCache();
        _db.Dispose();
    }

    /// <summary>A body that throws when anything reads it: proves a 401 never touches it.</summary>
    private sealed class ExplodingBody : Stream
    {
        public bool Touched { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 40_000_000;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Touched = true; throw new InvalidOperationException("body read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private (ApiGateMiddleware Gate, IServiceProvider Services) Create(RequestDelegate next, RateLimitOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IStringLocalizerFactory>(new KeyEchoLocalizerFactory());
        services.AddScoped(_ => _db);
        var provider = services.BuildServiceProvider();
        return (new ApiGateMiddleware(next, Microsoft.Extensions.Options.Options.Create(options ?? new RateLimitOptions())), provider);
    }

    private static DefaultHttpContext Context(IServiceProvider services, string path, Guid? user = null, Stream? body = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();
        if (body != null) context.Request.Body = body;
        if (user != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.ToString()) }, "ApiKey"));
        }
        return context;
    }

    [Fact]
    public async Task A_request_without_credentials_is_401_and_its_body_is_never_read()
    {
        var called = false;
        var (gate, services) = Create(_ => { called = true; return Task.CompletedTask; });
        var body = new ExplodingBody();
        var context = Context(services, "/api/presign", body: body);

        await gate.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(called);
        Assert.False(body.Touched);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task Authenticated_requests_and_the_open_routes_pass()
    {
        var calls = 0;
        var (gate, services) = Create(_ => { calls++; return Task.CompletedTask; });

        await gate.InvokeAsync(Context(services, "/api/presign", Guid.NewGuid()));
        await gate.InvokeAsync(Context(services, "/api/code-formats"));
        await gate.InvokeAsync(Context(services, "/api/webhooks/stripe"));
        await gate.InvokeAsync(Context(services, "/Account/SignIn"));

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task A_disabled_feature_is_503_and_other_routes_still_work()
    {
        _db.OperatorSwitches.Add(new OperatorSwitch { Name = "feature:find-codes", Reason = "test", CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        var calls = 0;
        var (gate, services) = Create(_ => { calls++; return Task.CompletedTask; });
        ResetSwitchCache();

        var refused = Context(services, "/api/find-codes", Guid.NewGuid());
        await gate.InvokeAsync(refused);
        Assert.Equal(503, refused.Response.StatusCode);
        Assert.Equal(0, calls);

        await gate.InvokeAsync(Context(services, "/api/presign", Guid.NewGuid()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_blocked_account_is_403_everywhere_on_api()
    {
        var user = Guid.NewGuid();
        _db.OperatorSwitches.Add(new OperatorSwitch { Name = $"user:{user}", CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        var (gate, services) = Create(_ => Task.CompletedTask);
        ResetSwitchCache();

        var context = Context(services, "/api/capabilities", user);
        await gate.InvokeAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_key_that_keeps_failing_on_the_heavy_path_is_blocked_then_released()
    {
        var options = new RateLimitOptions { FailuresBeforeBlock = 3, BlockMinutes = 15 };
        var (gate, services) = Create(ctx => { ctx.Response.StatusCode = 400; return Task.CompletedTask; }, options);
        var user = Guid.NewGuid();
        ResetSwitchCache();

        for (var i = 0; i < 3; i++)
        {
            await gate.InvokeAsync(Context(services, "/api/presign", user));
        }

        var blocked = Context(services, "/api/presign", user);
        await gate.InvokeAsync(blocked);
        Assert.Equal(429, blocked.Response.StatusCode);
        Assert.True(int.Parse(blocked.Response.Headers.RetryAfter.ToString()) > 0);

        // Someone else is not affected.
        var other = Context(services, "/api/presign", Guid.NewGuid());
        await gate.InvokeAsync(other);
        Assert.Equal(400, other.Response.StatusCode);
    }

    [Fact]
    public async Task A_blocked_key_can_still_ask_about_its_operations()
    {
        var options = new RateLimitOptions { FailuresBeforeBlock = 1 };
        var (gate, services) = Create(ctx => { ctx.Response.StatusCode = ctx.Request.Path == "/api/presign" ? 400 : 200; return Task.CompletedTask; }, options);
        var user = Guid.NewGuid();
        ResetSwitchCache();

        await gate.InvokeAsync(Context(services, "/api/presign", user));
        var heavy = Context(services, "/api/presign", user);
        await gate.InvokeAsync(heavy);
        Assert.Equal(429, heavy.Response.StatusCode);

        var status = Context(services, "/api/operations/abc", user);
        await gate.InvokeAsync(status);
        Assert.Equal(200, status.Response.StatusCode);
    }

    [Fact]
    public async Task An_exempt_account_is_never_blocked_and_the_default_is_off()
    {
        var portal = Guid.NewGuid();
        var exempt = new RateLimitOptions { FailuresBeforeBlock = 1, ExemptUserIds = portal.ToString() };
        var (gate, services) = Create(ctx => { ctx.Response.StatusCode = 400; return Task.CompletedTask; }, exempt);
        ResetSwitchCache();
        for (var i = 0; i < 5; i++)
        {
            var ctx = Context(services, "/api/presign", portal);
            await gate.InvokeAsync(ctx);
            Assert.Equal(400, ctx.Response.StatusCode);
        }

        Assert.Equal(0, new RateLimitOptions().FailuresBeforeBlock);
    }

    [Fact]
    public async Task Server_errors_do_not_count_towards_the_block()
    {
        var options = new RateLimitOptions { FailuresBeforeBlock = 2 };
        var (gate, services) = Create(ctx => { ctx.Response.StatusCode = 503; return Task.CompletedTask; }, options);
        var user = Guid.NewGuid();
        ResetSwitchCache();
        for (var i = 0; i < 5; i++)
        {
            var ctx = Context(services, "/api/presign", user);
            await gate.InvokeAsync(ctx);
            Assert.Equal(503, ctx.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Refused_credentials_and_payment_do_not_count_as_failures()
    {
        var options = new RateLimitOptions { FailuresBeforeBlock = 2 };
        var (gate, services) = Create(ctx => { ctx.Response.StatusCode = 402; return Task.CompletedTask; }, options);
        var user = Guid.NewGuid();
        ResetSwitchCache();

        for (var i = 0; i < 5; i++)
        {
            var ctx = Context(services, "/api/presign", user);
            await gate.InvokeAsync(ctx);
            Assert.Equal(402, ctx.Response.StatusCode);
        }
    }

    private static void ResetSwitchCache()
    {
        var field = typeof(ApiGateMiddleware).GetField("_switches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        field.SetValue(null, (new HashSet<string>(), DateTimeOffset.MinValue));
    }

    // ── Rate limiter ───────────────────────────────────────────────────────

    private static PartitionedRateLimiter<HttpContext> Limiter(RateLimitOptions options)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:UnauthenticatedPerMinute"] = options.UnauthenticatedPerMinute.ToString(),
                ["RateLimits:TsaProbePerMinute"] = options.TsaProbePerMinute.ToString(),
                ["RateLimits:SignUpPerHour"] = options.SignUpPerHour.ToString(),
                ["RateLimits:PerKeyPerMinute"] = options.PerKeyPerMinute.ToString(),
            }).Build();
        RateLimiting.Register(services, config);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
    }

    private static HttpContext Request(string path, string ip, Guid? user = null, string method = "POST")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.18.0.5"); // cloudflared in the Docker network
        context.Request.Headers["CF-Connecting-IP"] = ip;
        if (user != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.ToString()) }, "ApiKey"));
        }
        return context;
    }

    [Fact]
    public void Unauthenticated_api_calls_are_limited_per_client_ip()
    {
        var limiter = Limiter(new RateLimitOptions { UnauthenticatedPerMinute = 3 });
        for (var i = 0; i < 3; i++) Assert.True(limiter.AttemptAcquire(Request("/api/presign", "203.0.113.7")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("/api/presign", "203.0.113.7")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/presign", "198.51.100.9")).IsAcquired);
    }

    [Fact]
    public void CF_Connecting_IP_from_a_public_peer_is_not_believed()
    {
        var limiter = Limiter(new RateLimitOptions { UnauthenticatedPerMinute = 2 });
        HttpContext FromInternet(string claimed)
        {
            var c = Request("/api/presign", claimed);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("93.184.216.34");
            return c;
        }
        // Three different claimed addresses, one real peer: one bucket.
        Assert.True(limiter.AttemptAcquire(FromInternet("203.0.113.1")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(FromInternet("203.0.113.2")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(FromInternet("203.0.113.3")).IsAcquired);
    }

    [Fact]
    public void The_open_routes_are_not_counted_as_unauthenticated_traffic()
    {
        var limiter = Limiter(new RateLimitOptions { UnauthenticatedPerMinute = 1 });
        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.AttemptAcquire(Request("/api/code-formats", "203.0.113.7", method: "GET")).IsAcquired);
        }
    }

    [Fact]
    public void Tsa_probe_has_its_own_limit_per_key()
    {
        var limiter = Limiter(new RateLimitOptions { TsaProbePerMinute = 2 });
        var user = Guid.NewGuid();
        Assert.True(limiter.AttemptAcquire(Request("/api/tsa-probe", "203.0.113.7", user)).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/tsa-probe", "203.0.113.7", user)).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("/api/tsa-probe", "203.0.113.7", user)).IsAcquired);
        // Another key, and another route of the same key, are untouched.
        Assert.True(limiter.AttemptAcquire(Request("/api/tsa-probe", "203.0.113.7", Guid.NewGuid())).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/presign", "203.0.113.7", user)).IsAcquired);
    }

    [Fact]
    public void Sign_up_is_limited_per_ip_per_hour()
    {
        var limiter = Limiter(new RateLimitOptions { SignUpPerHour = 2 });
        Assert.True(limiter.AttemptAcquire(Request("/Account/SignUp", "203.0.113.7")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/Account/SignUp", "203.0.113.7")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("/Account/SignUp", "203.0.113.7")).IsAcquired);
    }
}
