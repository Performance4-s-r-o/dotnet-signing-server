using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using DotNetSigningServer.Filters;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #80 against the whole app on PostgreSQL: an existing cookie session follows deactivation and
/// demotion on the very next request, whichever write path made the change; browser tokens stay
/// in scope; re-auth guards token creation; the audit table and the limiter live in the database.
/// </summary>
[Trait("Category", "Db")]
public class SessionRevalidationTests : IClassFixture<WebUiAuthFixture>
{
    private readonly WebUiAuthFixture _fx;

    public SessionRevalidationTests(WebUiAuthFixture fx)
    {
        _fx = fx;
    }

    private async Task<Guid> NewUserAsync(bool admin = false)
    {
        await using var db = _fx.Postgres.CreateContext();
        var user = new User { Email = $"u{Guid.NewGuid():N}@example.com", EmailVerified = true, IsActive = true, IsAdmin = admin };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private HttpClient CookieClient(string cookie)
    {
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static bool SentToSignIn(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found
        && (r.Headers.Location?.ToString() ?? "").Contains("/Account/SignIn", StringComparison.OrdinalIgnoreCase);

    private static bool Denied(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.Forbidden
        || (r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found
            && ((r.Headers.Location?.ToString() ?? "").Contains("/Account/Denied", StringComparison.OrdinalIgnoreCase)
                || (r.Headers.Location?.ToString() ?? "").Contains("/Account/SignIn", StringComparison.OrdinalIgnoreCase)));

    [DockerFact]
    public async Task Deactivation_InSql_WithoutTouchingTheStamp_RejectsTheNextRequest()
    {
        var id = await NewUserAsync();
        var client = CookieClient(_fx.CookieHeaderFor(id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ApiTokens")).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            // Another write path that does not know about the stamp.
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.True(SentToSignIn(await client.GetAsync("/ApiTokens")));
    }

    [DockerFact]
    public async Task Deactivation_ByAnAdminThroughEf_RejectsTheNextRequest()
    {
        var id = await NewUserAsync();
        var client = CookieClient(_fx.CookieHeaderFor(id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ApiTokens")).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            user.IsActive = false; // the admin's code path does not touch UpdatedAt
            await db.SaveChangesAsync();
        }

        Assert.True(SentToSignIn(await client.GetAsync("/ApiTokens")));
    }

    [DockerFact]
    public async Task Demotion_InSql_ClosesAdmin_OnTheNextRequest()
    {
        var id = await NewUserAsync(admin: true);
        var client = CookieClient(_fx.CookieHeaderFor(id, admin: true));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, false));
        }

        Assert.True(Denied(await client.GetAsync("/Admin")));
        // The session itself survives a demotion: only the role went.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ApiTokens")).StatusCode);
    }

    [DockerFact]
    public async Task Demotion_ThroughEf_ClosesAdmin_OnTheNextRequest()
    {
        var id = await NewUserAsync(admin: true);
        var client = CookieClient(_fx.CookieHeaderFor(id, admin: true));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            user.IsAdmin = false;
            await db.SaveChangesAsync();
        }

        Assert.True(Denied(await client.GetAsync("/Admin")));
    }

    [DockerFact]
    public async Task Cookie_WithoutStamp_IsRejected()
    {
        // A ticket without SecurityStamp used to skip the check altogether.
        var client = CookieClient(_fx.CookieHeaderFor(await NewUserAsync(), withStamp: false));
        Assert.True(SentToSignIn(await client.GetAsync("/ApiTokens")));
    }

    private async Task<string> BrowserTokenAsync(string origin)
    {
        var id = await NewUserAsync();
        using var scope = _fx.App.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        await using var db = _fx.Postgres.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        var (plaintext, hash, _) = tokens.IssueToken(user, "web", null);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = id, Label = "web", TokenHash = hash, TokenPrefix = plaintext[..8],
            IsBrowserToken = true, AllowedOrigins = origin,
        });
        await db.SaveChangesAsync();
        return plaintext;
    }

    [DockerFact]
    public async Task BrowserToken_OutsideItsScope_Gets403_EvenFromItsOwnOrigin()
    {
        const string origin = "https://app.example.com";
        var token = await BrowserTokenAsync(origin);
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Origin", origin);

        var templates = await client.GetAsync("/api/pdf-template");
        Assert.Equal(HttpStatusCode.Forbidden, templates.StatusCode);
        Assert.Contains(BrowserTokenScope.ScopeErrorCode, await templates.Content.ReadAsStringAsync());

        var fill = await client.PostAsync("/api/fill-pdf", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, fill.StatusCode);

        // In scope: authenticated and past the gate (the empty body is then refused as input).
        var presign = await client.PostAsync("/api/presign", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.Forbidden, presign.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, presign.StatusCode);
    }

    [DockerFact]
    public async Task ServerToken_IsNotScoped()
    {
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fx.ApiToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pdf-template")).StatusCode);
    }

    [DockerFact]
    public async Task BrowserToken_Budget_IsReservedAtomicallyAndReleased()
    {
        var id = await NewUserAsync();
        await using var db = _fx.Postgres.CreateContext();
        var token = new ApiToken { UserId = id, Label = "web", TokenHash = new byte[] { 9 }, IsBrowserToken = true, CreditLimit = 2 };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        Assert.True(await BrowserTokenScope.TryReserveAsync(db, token.Id, 1));
        Assert.False(await BrowserTokenScope.TryReserveAsync(db, token.Id, 2));
        await BrowserTokenScope.ReleaseAsync(db, token.Id, 1);
        Assert.True(await BrowserTokenScope.TryReserveAsync(db, token.Id, 2));
        Assert.Equal(2, (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id)).CreditsUsed);
    }

    [DockerFact]
    public async Task BrowserToken_Budget_IsReturned_WhenTheOperationThrowsOrIsAborted_AndKeptWhenCommitted()
    {
        var id = await NewUserAsync();
        await using var db = _fx.Postgres.CreateContext();
        var token = new ApiToken { UserId = id, Label = "web", TokenHash = new byte[] { 7 }, IsBrowserToken = true, CreditLimit = 5 };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
        async Task<int> Used() => (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id)).CreditsUsed;

        // Completing the operation throws (CompleteOperationAsync, a lost lease, …).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var budget = await BrowserTokenBudget.TryReserveAsync(db, token.Id, 2);
            Assert.NotNull(budget);
            Assert.Equal(2, await Used());
            throw new InvalidOperationException("completion failed");
        });
        Assert.Equal(0, await Used());

        // An aborted request leaves without committing.
        await using (var aborted = await BrowserTokenBudget.TryReserveAsync(db, token.Id, 3)) { }
        Assert.Equal(0, await Used());

        // Completed: the credits stay spent.
        await using (var done = await BrowserTokenBudget.TryReserveAsync(db, token.Id, 4)) { done!.Commit(); }
        Assert.Equal(4, await Used());
        Assert.Null(await BrowserTokenBudget.TryReserveAsync(db, token.Id, 2)); // 4 + 2 > 5
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<HttpResponseMessage> CreateTokenAsync(Guid userId, DateTimeOffset? authTime)
    {
        var client = _fx.App.CreateClient();
        var session = _fx.CookieHeaderFor(userId, authTime: authTime);
        var page = new HttpRequestMessage(HttpMethod.Get, "/ApiTokens");
        page.Headers.Add("Cookie", session);
        var pageResponse = await client.SendAsync(page);
        var html = await pageResponse.Content.ReadAsStringAsync();
        var antiforgeryCookie = pageResponse.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0]).First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));

        var post = new HttpRequestMessage(HttpMethod.Post, "/ApiTokens")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["label"] = "step-up",
                ["usageType"] = "server",
                ["__RequestVerificationToken"] = AntiforgeryToken(html),
            }),
        };
        post.Headers.Add("Cookie", $"{session}; {antiforgeryCookie}");
        post.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(post);
    }

    [DockerFact]
    public async Task CreatingAnApiToken_WithoutRecentReauth_Is403ReauthRequired()
    {
        var id = await NewUserAsync();
        var stale = await CreateTokenAsync(id, DateTimeOffset.UtcNow.AddMinutes(-16));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Contains(RequireRecentAuthAttribute.ErrorCode, await stale.Content.ReadAsStringAsync());

        var none = await CreateTokenAsync(id, authTime: null); // sessions from before #80
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);

        await using var db = _fx.Postgres.CreateContext();
        Assert.False(await db.ApiTokens.AnyAsync(t => t.UserId == id));
    }

    [DockerFact]
    public async Task CreatingAnApiToken_WithinTheWindow_Works()
    {
        var id = await NewUserAsync();
        var fresh = await CreateTokenAsync(id, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(HttpStatusCode.Redirect, fresh.StatusCode);

        await using var db = _fx.Postgres.CreateContext();
        Assert.True(await db.ApiTokens.AnyAsync(t => t.UserId == id && t.Label == "step-up"));
    }

    [DockerFact]
    public async Task AdminAudit_IsAppendOnly_InTheDatabase()
    {
        await using var db = _fx.Postgres.CreateContext();
        var entry = new AdminAuditEntry { CreatedAt = DateTimeOffset.UtcNow, ActorUserId = Guid.NewGuid(), Action = "test", Change = "{}" };
        db.AdminAuditEntries.Add(entry);
        await db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE \"AdminAuditEntries\" SET \"Action\" = 'x' WHERE \"Id\" = {0}", entry.Id));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM \"AdminAuditEntries\" WHERE \"Id\" = {0}", entry.Id));
        Assert.True(await db.AdminAuditEntries.AnyAsync(a => a.Id == entry.Id));
    }

    [DockerFact]
    public async Task LoginChallenge_IsIndexed()
    {
        await using var db = _fx.Postgres.CreateContext();
        var definitions = await db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'dotnet_signing' AND indexname = 'IX_Users_LoginChallengeHash'")
            .ToListAsync();
        var definition = Assert.Single(definitions);
        Assert.Contains("IS NOT NULL", definition);
    }

    [DockerFact]
    public async Task AttemptLimiter_IsShared_AcrossContexts()
    {
        var key = $"test:{Guid.NewGuid():N}";
        for (var i = 0; i < AuthAttemptLimiter.DefaultMaxAttempts; i++)
        {
            // A new context each time stands for another request or another instance.
            await using var db = _fx.Postgres.CreateContext();
            Assert.False(await new AuthAttemptLimiter(db).IsLimitedAsync(key));
        }
        await using var last = _fx.Postgres.CreateContext();
        Assert.True(await new AuthAttemptLimiter(last).IsLimitedAsync(key));
    }
}
