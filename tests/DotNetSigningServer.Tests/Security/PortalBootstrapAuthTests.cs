using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Security;

/// <summary>The bootstrap service account against the whole app (#169): API only, never the web UI.</summary>
[Trait("Category", "Db")]
public class PortalBootstrapAuthTests : IClassFixture<WebUiAuthFixture>
{
    private const string Token = "q7Hn2xV9mLp4RtYb8KcZ3wFd6JgSe1Ua";
    private readonly WebUiAuthFixture _fx;

    public PortalBootstrapAuthTests(WebUiAuthFixture fx) => _fx = fx;

    private async Task<Guid> BootstrapAsync()
    {
        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DotNetSigningServer.Data.ApplicationDbContext>();
        await PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = Token }, new PrivateServerOptions(),
            scope.ServiceProvider.GetRequiredService<ITokenService>(), db, NullLogger.Instance);
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Email == PortalBootstrap.AccountEmail)).Id;
    }

    private static string Antiforgery(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> form, string? formPage = null)
    {
        var client = _fx.App.CreateClient();
        var page = await client.GetAsync(formPage ?? path);
        var html = await page.Content.ReadAsStringAsync();
        var cookie = page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])
            .First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        form["__RequestVerificationToken"] = Antiforgery(html);
        var post = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        post.Headers.Add("Cookie", cookie);
        return await client.SendAsync(post);
    }

    [DockerFact]
    public async Task Bootstrap_token_authorises_a_protected_api_route()
    {
        await BootstrapAsync();
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pdf-template")).StatusCode);

        var wrong = _fx.App.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token + "x");
        Assert.NotEqual(HttpStatusCode.OK, (await wrong.GetAsync("/api/pdf-template")).StatusCode);
    }

    [DockerFact]
    public async Task Service_account_cannot_sign_in_whatever_the_password()
    {
        var id = await BootstrapAsync();
        await using (var db = _fx.Postgres.CreateContext())
        {
            // Even a verified, active account with a password it knows.
            var auth = new AuthService();
            var (h, s, i) = auth.HashPassword("known-password-123");
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x
                .SetProperty(u => u.EmailVerified, true)
                .SetProperty(u => u.PasswordHash, h).SetProperty(u => u.PasswordSalt, s).SetProperty(u => u.PasswordIterations, i));
        }

        var response = await PostFormAsync("/Account/SignIn", new()
        {
            ["Email"] = PortalBootstrap.AccountEmail,
            ["Password"] = "known-password-123",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again, no redirect to the code step
        await using var check = _fx.Postgres.CreateContext();
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.Null(user.EmailOtpCode);
        Assert.Null(user.LoginChallengeHash);
    }

    [DockerFact]
    public async Task Service_account_gets_no_password_reset()
    {
        var id = await BootstrapAsync();
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailVerified, true));
        }
        await PostFormAsync("/Account/ForgotPassword", new() { ["Email"] = PortalBootstrap.AccountEmail });

        await using var check = _fx.Postgres.CreateContext();
        Assert.Null((await check.Users.AsNoTracking().SingleAsync(u => u.Id == id)).PasswordResetToken);

        // A reset token planted straight in the database is refused too.
        var planted = SecureTokens.Generate();
        await check.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.PasswordResetToken, SecureTokens.Hash(planted))
            .SetProperty(u => u.PasswordResetExpiresAt, DateTimeOffset.UtcNow.AddHours(1)));
        var before = (await check.Users.AsNoTracking().SingleAsync(u => u.Id == id)).PasswordHash;
        await PostFormAsync("/Account/ResetPassword", new()
        {
            ["Token"] = planted, ["Password"] = "Another-password-123", ["ConfirmPassword"] = "Another-password-123",
        }, formPage: "/Account/ResetPassword?token=" + planted);
        Assert.Equal(before, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == id)).PasswordHash);
    }

    [DockerFact]
    public async Task Service_account_has_no_cookie_session_and_no_code_step()
    {
        var id = await BootstrapAsync();
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailVerified, true));
        }

        // A session cookie minted for it is rejected at once.
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", _fx.CookieHeaderFor(id));
        var r = await client.GetAsync("/ApiTokens");
        Assert.Contains("/Account/SignIn", r.Headers.Location?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        // A waiting code and challenge planted in the database do not open it.
        var challenge = SecureTokens.Generate();
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x
                .SetProperty(u => u.LoginChallengeHash, SecureTokens.Hash(challenge))
                .SetProperty(u => u.EmailOtpCode, SecureTokens.Hash("123456"))
                .SetProperty(u => u.EmailOtpExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
        }
        var verify = await PostFormWithCookieAsync("/Account/TwoFactor", challenge);
        Assert.DoesNotContain(".AspNetCore.Cookies", string.Join(';', verify.Headers.TryGetValues("Set-Cookie", out var c) ? c : []));
    }

    private async Task<HttpResponseMessage> PostFormWithCookieAsync(string path, string challenge)
    {
        var client = _fx.App.CreateClient();
        var page = await client.GetAsync("/Account/SignIn");
        var html = await page.Content.ReadAsStringAsync();
        var af = page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])
            .First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        var post = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = "123456", ["__RequestVerificationToken"] = Antiforgery(html),
            }),
        };
        post.Headers.Add("Cookie", $"{af}; {AccountController.LoginChallengeCookie}={challenge}");
        return await client.SendAsync(post);
    }
}
