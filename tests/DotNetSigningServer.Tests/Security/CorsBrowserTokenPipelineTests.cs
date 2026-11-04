using System.Net;
using System.Net.Http.Headers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Security;

/// <summary>#177 through the whole pipeline: the preflight by token origins, the real call by its own token.</summary>
[Trait("Category", "Db")]
public class CorsBrowserTokenPipelineTests : IClassFixture<WebUiAuthFixture>
{
    private const string Shop = "https://shop.example";
    private const string Other = "https://other.example";
    private readonly WebUiAuthFixture _fx;

    public CorsBrowserTokenPipelineTests(WebUiAuthFixture fx) => _fx = fx;

    private async Task<string> BrowserTokenAsync(string origin)
    {
        await using var db = _fx.Postgres.CreateContext();
        var user = db.Users.Single(u => u.Id == _fx.UserId);
        using var scope = _fx.App.Services.CreateScope();
        var (plaintext, hash, _) = scope.ServiceProvider.GetRequiredService<ITokenService>().IssueToken(user, "browser", null);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = "browser " + origin,
            TokenHash = hash,
            TokenPrefix = plaintext[..8],
            IsBrowserToken = true,
            AllowedOrigins = origin,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _fx.App.Services.GetRequiredService<IAllowedOriginService>().InvalidateTokenOrigins();
        return plaintext;
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/presign");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return request;
    }

    private static HttpRequestMessage Presign(string token, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/presign")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Origin", origin);
        return request;
    }

    [DockerFact]
    public async Task Preflight_KnownOriginPasses_UnknownIsRefused()
    {
        await BrowserTokenAsync(Shop);
        var client = _fx.App.CreateClient();

        var known = await client.SendAsync(Preflight(Shop));
        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);
        Assert.Equal(Shop, known.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(known.Headers.Contains("Access-Control-Allow-Credentials"));

        var unknown = await client.SendAsync(Preflight("https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.False(unknown.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [DockerFact]
    public async Task RealCall_WithTheOriginOfAnotherToken_IsRefusedReadably()
    {
        var shopToken = await BrowserTokenAsync(Shop);
        await BrowserTokenAsync(Other);
        var client = _fx.App.CreateClient();

        // Other passes the preflight (another token lists it), but this token is locked to Shop.
        var response = await client.SendAsync(Presign(shopToken, Other));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Other, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [DockerFact]
    public async Task RealCall_FromItsOwnOrigin_PassesAuthentication()
    {
        var token = await BrowserTokenAsync(Shop);

        var response = await _fx.App.CreateClient().SendAsync(Presign(token, Shop));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DockerFact]
    public async Task BrowserToken_OutsideItsScope_IsTokenScope()
    {
        var token = await BrowserTokenAsync(Shop);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/pdf-template");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Origin", Shop);

        var response = await _fx.App.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(BrowserTokenScope.ScopeErrorCode, await response.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task ServerToken_FromABrowserOrigin_IsRefused()
    {
        await BrowserTokenAsync(Shop);

        var response = await _fx.App.CreateClient().SendAsync(Presign(_fx.ApiToken, Shop));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DockerFact]
    public async Task SessionCookie_FromAnotherOrigin_DoesNotAuthenticate()
    {
        await BrowserTokenAsync(Shop);
        var path = $"/api/pdf-template/{Guid.NewGuid()}";
        var crossSite = new HttpRequestMessage(HttpMethod.Get, path);
        crossSite.Headers.Add("Cookie", _fx.SessionCookieHeader());
        crossSite.Headers.Add("Origin", Shop);
        var sameSite = new HttpRequestMessage(HttpMethod.Get, path);
        sameSite.Headers.Add("Cookie", _fx.SessionCookieHeader());

        var client = _fx.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(crossSite)).StatusCode);
        // Same origin: past authentication (an unknown id then fails in the handler).
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(sameSite)).StatusCode);
    }
}
