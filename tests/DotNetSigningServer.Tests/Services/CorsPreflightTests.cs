using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#177: the CORS preflight is decided by the origins of active browser tokens.</summary>
public class CorsPreflightTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _provider;

    public CorsPreflightTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    private AllowedOriginService Service(string environment = "Production", string? configured = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configured is null ? [] : new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = configured })
            .Build();
        return new AllowedOriginService(_provider.GetRequiredService<IServiceScopeFactory>(),
            new TestHostEnvironment { EnvironmentName = environment }, config);
    }

    private void AddToken(string? origins, bool browser = true, bool revoked = false, DateTimeOffset? expires = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = TestHelpers.CreateTestUser();
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = "t",
            TokenHash = Guid.NewGuid().ToByteArray(),
            IsBrowserToken = browser,
            AllowedOrigins = origins,
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
            ExpiresAt = expires,
        });
        db.SaveChanges();
    }

    private static HttpContext Request(string method = "OPTIONS", string path = "/api/presign", string? origin = null, string host = "sign.example.com")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        if (origin != null) ctx.Request.Headers.Origin = origin;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public void OriginOfAnActiveBrowserToken_Passes()
    {
        AddToken("https://shop.example\nhttps://other.example");

        Assert.True(Service().IsOriginAllowed("https://shop.example", Request()));
        Assert.True(Service().IsOriginAllowed("https://other.example:443", Request()));
    }

    [Fact]
    public void UnknownOrigin_IsRefused()
    {
        AddToken("https://shop.example");

        Assert.False(Service().IsOriginAllowed("https://evil.example", Request()));
        Assert.False(Service().IsOriginAllowed("https://shop.example.evil.example", Request()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("not a url")]
    [InlineData("file://")]
    [InlineData("ftp://shop.example")]
    [InlineData("https://shop.example/")] // a serialized origin has no trailing slash
    [InlineData("https://shop.example/path")]
    [InlineData("https://shop.example?x=1")]
    [InlineData("https://user@shop.example")]
    public void NotASerializedOrigin_IsRefused(string origin)
    {
        AddToken("https://shop.example");

        Assert.False(Service().IsOriginAllowed(origin, Request()));
        Assert.False(Service("Development").IsOriginAllowed(origin, Request()));
    }

    [Theory]
    [InlineData("https://SHOP.example", true)]
    [InlineData("https://shop.example:443", true)]
    [InlineData("https://shop.example:8443", false)]
    [InlineData("http://shop.example", false)]
    [InlineData("https://api.shop.example", false)]
    [InlineData("https://example", false)]
    public void OriginVariants(string origin, bool allowed)
    {
        AddToken("https://shop.example");

        Assert.Equal(allowed, Service().IsOriginAllowed(origin, Request()));
    }

    [Fact]
    public void NullOrigin_IsNeverTheTokensOrigin()
    {
        var token = new ApiToken { IsBrowserToken = true, AllowedOrigins = "https://shop.example" };

        Assert.False(Service("Development").IsOriginAllowedForToken("null", token));
        Assert.False(Service().IsOriginAllowedForToken("https://shop.example/path", token));
        Assert.True(Service().IsOriginAllowedForToken("https://SHOP.example:443", token));
    }

    [Fact]
    public void RevokedExpiredAndServerTokens_DoNotCount()
    {
        AddToken("https://revoked.example", revoked: true);
        AddToken("https://expired.example", expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        AddToken("https://server.example", browser: false);

        var sut = Service();
        Assert.False(sut.IsOriginAllowed("https://revoked.example", Request()));
        Assert.False(sut.IsOriginAllowed("https://expired.example", Request()));
        Assert.False(sut.IsOriginAllowed("https://server.example", Request()));
    }

    [Fact]
    public void OwnOrigin_AndNoOrigin_Pass()
    {
        var sut = Service();

        Assert.True(sut.IsOriginAllowed("https://sign.example.com", Request()));
        Assert.True(sut.IsOriginAllowed("", Request()));
    }

    [Fact]
    public void Localhost_PassesOnlyInDevelopmentOrOnAToken()
    {
        Assert.False(Service().IsOriginAllowed("http://localhost:3000", Request()));
        Assert.True(Service("Development").IsOriginAllowed("http://localhost:3000", Request()));

        AddToken("https://localhost:4321");
        Assert.True(Service().IsOriginAllowed("https://localhost:4321", Request()));
    }

    [Fact]
    public void ConfiguredList_OnlyNarrows()
    {
        AddToken("https://shop.example\nhttps://other.example");
        var sut = Service(configured: "https://shop.example,https://listed-only.example");

        Assert.True(sut.IsOriginAllowed("https://shop.example", Request()));
        Assert.False(sut.IsOriginAllowed("https://other.example", Request()));
        // Listed by the operator but on no token: nothing to call with, so no preflight either.
        Assert.False(sut.IsOriginAllowed("https://listed-only.example", Request()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{{environment.DOTNET_CORS_ALLOWED_ORIGINS}}")]
    public void EmptyOrUnresolvedList_LeavesItToTheTokens(string configured)
    {
        AddToken("https://shop.example");

        Assert.True(Service(configured: configured).IsOriginAllowed("https://shop.example", Request()));
    }

    [Fact]
    public void NewToken_IsCachedUntilInvalidated()
    {
        var sut = Service();
        Assert.False(sut.IsOriginAllowed("https://late.example", Request()));

        AddToken("https://late.example");
        Assert.False(sut.IsOriginAllowed("https://late.example", Request()));

        sut.InvalidateTokenOrigins();
        Assert.True(sut.IsOriginAllowed("https://late.example", Request()));
    }

    private async Task<HttpContext> RunAsync(HttpContext ctx, AllowedOriginService? origins = null)
    {
        var reached = false;
        var middleware = new ApiCorsMiddleware(_ => { reached = true; return Task.CompletedTask; }, NullLogger<ApiCorsMiddleware>.Instance);
        await middleware.InvokeAsync(ctx, origins ?? Service());
        ctx.Items["reached"] = reached;
        return ctx;
    }

    [Fact]
    public async Task Preflight_KnownOrigin_204WithCorsHeadersButNoCredentials()
    {
        AddToken("https://shop.example");

        var ctx = await RunAsync(Request(origin: "https://shop.example"));

        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        Assert.Equal("https://shop.example", ctx.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Equal("Origin", ctx.Response.Headers.Vary.ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.False((bool)ctx.Items["reached"]!);
    }

    [Fact]
    public async Task Preflight_UnknownOrigin_403WithoutCorsHeaders()
    {
        AddToken("https://shop.example");

        var ctx = await RunAsync(Request(origin: "https://evil.example"));

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False((bool)ctx.Items["reached"]!);
    }

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("POST")]
    public async Task NullOrigin_403WithoutCorsHeaders(string method)
    {
        AddToken("https://shop.example");

        var ctx = await RunAsync(Request(method, origin: "null"));

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False((bool)ctx.Items["reached"]!);
    }

    [Fact]
    public async Task CrossOriginRequest_LosesTheSessionCookie()
    {
        AddToken("https://shop.example");
        var ctx = Request("POST", "/api/pdf-template", "https://shop.example");
        ctx.Request.Headers.Cookie = ".AspNetCore.Cookies=session";

        await RunAsync(ctx);

        Assert.True((bool)ctx.Items["reached"]!);
        Assert.False(ctx.Request.Headers.ContainsKey("Cookie"));
        Assert.Equal("https://shop.example", ctx.Response.Headers.AccessControlAllowOrigin.ToString());
    }

    [Fact]
    public async Task SameOriginRequest_KeepsTheSessionCookie()
    {
        var ctx = Request("POST", "/api/pdf-template", "https://sign.example.com");
        ctx.Request.Headers.Cookie = ".AspNetCore.Cookies=session";

        await RunAsync(ctx);

        Assert.True((bool)ctx.Items["reached"]!);
        Assert.Equal(".AspNetCore.Cookies=session", ctx.Request.Headers.Cookie.ToString());
    }

    [Fact]
    public async Task OtherPaths_AreUntouched()
    {
        var ctx = Request("OPTIONS", "/Account/SignIn", "https://evil.example");

        await RunAsync(ctx);

        Assert.True((bool)ctx.Items["reached"]!);
        Assert.False(ctx.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }
}
