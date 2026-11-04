using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Ops;
using DotNetSigningServer.Services.Provisioning;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// The provisioning API and provisioned accounts against the whole app on PostgreSQL (#131):
/// idempotence, conflicts, the scope of the main key, limits, audit, inheritance, tenant
/// isolation, rotation and deletion.
/// </summary>
[Trait("Category", "Db")]
public class ProvisioningApiTests : IClassFixture<WebUiAuthFixture>
{
    private readonly WebUiAuthFixture _fx;

    public ProvisioningApiTests(WebUiAuthFixture fx) => _fx = fx;

    private sealed class CapturingAlerter : IOpsAlerter
    {
        public ConcurrentQueue<string> Keys { get; } = new();
        public void Alert(string key, string text) => Keys.Enqueue(key);
    }

    private WebApplicationFactory<Program> App(ProvisioningOptions? options = null, IOpsAlerter? alerter = null) =>
        _fx.App.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ProvisioningOptions>();
            services.AddSingleton(options ?? new ProvisioningOptions { MaxAccountsPerDay = 0, AlertAccountsPerHour = 0, CallsPerMinute = 1000 });
            if (alerter != null)
            {
                services.RemoveAll<IOpsAlerter>();
                services.AddSingleton(alerter);
            }
        }));

    /// <summary>32 random bytes, base64url: what the integrator generates.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewExternalId() => Guid.NewGuid().ToString();

    /// <summary>A parent of its own per test, so limits and listings do not mix between tests.</summary>
    private async Task<(Guid Id, string Token)> NewParentAsync(WebApplicationFactory<Program> app, bool enterprise = true, bool sealAllowed = true)
    {
        var token = NewToken();
        using var scope = app.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        await using var db = _fx.Postgres.CreateContext();
        var parent = new User
        {
            Email = $"parent-{Guid.NewGuid():N}@service.invalid",
            IsActive = true,
            IsServiceAccount = true,
            IsEnterprise = enterprise,
            SealAllowed = sealAllowed,
        };
        db.Users.Add(parent);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = parent.Id,
            Label = PortalBootstrap.TokenLabel,
            TokenHash = tokens.HashToken(ApiAuthService.NormalizeToken(token)),
            IsProvisioningToken = true,
        });
        await db.SaveChangesAsync();
        return (parent.Id, token);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> app, HttpMethod method, string path, string token, object? body = null)
    {
        var client = app.CreateClient();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ProvisionAsync(
        WebApplicationFactory<Program> app, string mainKey, string externalId, string apiToken, string? label = null) =>
        SendAsync(app, HttpMethod.Post, "/api/provisioning/accounts", mainKey, new { externalId, label, apiToken });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private async Task<List<AccountProvisioningEvent>> EventsAsync(Guid parentId)
    {
        await using var db = _fx.Postgres.CreateContext();
        return await db.AccountProvisioningEvents.AsNoTracking().Where(e => e.CallerUserId == parentId).OrderBy(e => e.Id).ToListAsync();
    }

    private static object PresignBody()
    {
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        return new
        {
            pdfContent = TestHelpers.CreateMinimalPdfBase64(),
            certificatePem = certPem,
            signRect = new { x = 10, y = 10, width = 200, height = 50 },
        };
    }

    // ── Idempotence and conflicts ───────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Create_then_repeat_with_the_same_token_is_201_then_200_with_the_same_account()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var externalId = NewExternalId();
        var childToken = NewToken();

        var first = await ProvisionAsync(app, mainKey, externalId, childToken, "Contoso s.r.o.");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await JsonAsync(first);
        var second = await ProvisionAsync(app, mainKey, externalId, childToken, "Contoso s.r.o.");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replayed = await JsonAsync(second);

        Assert.Equal(created.GetProperty("accountId").GetGuid(), replayed.GetProperty("accountId").GetGuid());
        Assert.Equal(externalId, replayed.GetProperty("externalId").GetString());
        Assert.Equal("Contoso s.r.o.", replayed.GetProperty("label").GetString());
        Assert.True(replayed.GetProperty("inherits").GetProperty("enterprise").GetBoolean());
        Assert.True(replayed.GetProperty("inherits").GetProperty("sealAllowed").GetBoolean());
        Assert.DoesNotContain(childToken, await second.Content.ReadAsStringAsync());

        // The child's token works as an ordinary server token.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", childToken)).StatusCode);

        await using var db = _fx.Postgres.CreateContext();
        var child = await db.Users.AsNoTracking().SingleAsync(u => u.ParentUserId == parentId);
        Assert.True(child.IsServiceAccount);
        Assert.False(child.IsEnterprise); // inherited, never its own
        Assert.False(child.SealAllowed);
        Assert.EndsWith("@service.invalid", child.Email);
        var token = await db.ApiTokens.AsNoTracking().SingleAsync(t => t.UserId == child.Id);
        Assert.Null(token.TokenPrefix);
        Assert.False(token.IsProvisioningToken);

        Assert.Equal(new[] { AccountProvisioningOutcomes.Created, AccountProvisioningOutcomes.Replayed },
            (await EventsAsync(parentId)).Select(e => e.Outcome));
        Assert.All(await EventsAsync(parentId), e => Assert.Equal(child.Id, e.ChildUserId));
    }

    [DockerFact]
    public async Task Another_token_for_an_existing_account_or_a_token_in_use_is_409_and_issues_nothing()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var externalId = NewExternalId();
        var childToken = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, externalId, childToken)).StatusCode);

        var otherToken = NewToken();
        var conflict = await ProvisionAsync(app, mainKey, externalId, otherToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("ACCOUNT_EXISTS", await CodeAsync(conflict));
        Assert.False((await JsonAsync(conflict)).TryGetProperty("accountId", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", otherToken)).StatusCode);

        // The same token for another tenant: never a second meaning.
        var reused = await ProvisionAsync(app, mainKey, NewExternalId(), childToken);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("ACCOUNT_EXISTS", await CodeAsync(reused));
        // The main key itself cannot become a tenant's key.
        Assert.Equal(HttpStatusCode.Conflict, (await ProvisionAsync(app, mainKey, NewExternalId(), mainKey)).StatusCode);

        await using var db = _fx.Postgres.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.ParentUserId == parentId));
        Assert.Equal(3, (await EventsAsync(parentId)).Count(e => e.Outcome == AccountProvisioningOutcomes.Conflict));
    }

    [DockerFact]
    public async Task Invalid_input_is_400_and_creates_nothing()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);

        var cases = new (object Body, string Code)[]
        {
            (new { externalId = "", apiToken = NewToken() }, "EXTERNAL_ID_INVALID"),
            (new { externalId = "with space", apiToken = NewToken() }, "EXTERNAL_ID_INVALID"),
            (new { externalId = new string('a', 65), apiToken = NewToken() }, "EXTERNAL_ID_INVALID"),
            (new { externalId = NewExternalId(), label = new string('x', 129), apiToken = NewToken() }, "LABEL_INVALID"),
            (new { externalId = NewExternalId(), apiToken = "short" }, "TOKEN_WEAK"),
            (new { externalId = NewExternalId(), apiToken = new string('a', 50) }, "TOKEN_WEAK"),
            (new { externalId = NewExternalId() }, "TOKEN_WEAK"),
        };
        foreach (var (body, code) in cases)
        {
            var response = await SendAsync(app, HttpMethod.Post, "/api/provisioning/accounts", mainKey, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, await CodeAsync(response));
        }

        await using var db = _fx.Postgres.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.ParentUserId == parentId));
        Assert.All(await EventsAsync(parentId), e => Assert.Equal(AccountProvisioningOutcomes.Invalid, e.Outcome));
    }

    [DockerFact]
    public async Task A_parent_without_enterprise_is_refused()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app, enterprise: false);

        var response = await ProvisionAsync(app, mainKey, NewExternalId(), NewToken());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PARENT_NOT_ENTERPRISE", await CodeAsync(response));
        await using var db = _fx.Postgres.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.ParentUserId == parentId));
        Assert.Equal(AccountProvisioningOutcomes.RefusedParent, Assert.Single(await EventsAsync(parentId)).Outcome);
    }

    // ── Scope of the main key ───────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task The_main_key_provisions_only_and_only_it_provisions()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var childToken = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, NewExternalId(), childToken)).StatusCode);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/presign"), (HttpMethod.Post, "/api/sign"), (HttpMethod.Get, "/api/pdf-template"),
                     (HttpMethod.Get, "/api/operations/x"), (HttpMethod.Post, "/api/account/tokens/rotate"), (HttpMethod.Delete, "/api/account"),
                 })
        {
            var refused = await SendAsync(app, method, path, mainKey, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("TOKEN_SCOPE", await CodeAsync(refused));
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/capabilities", mainKey)).StatusCode);

        // A tenant's key, and any ordinary account's key, gets nothing from the provisioning API.
        foreach (var other in new[] { childToken, _fx.ApiToken })
        {
            var create = await ProvisionAsync(app, other, NewExternalId(), NewToken());
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Equal("TOKEN_SCOPE", await CodeAsync(create));
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, HttpMethod.Get, "/api/provisioning/accounts", other)).StatusCode);
        }

        await using var db = _fx.Postgres.CreateContext();
        Assert.Equal(6, (await EventsAsync(parentId)).Count(e => e.Outcome == AccountProvisioningOutcomes.RefusedScope));
        Assert.True(await db.AccountProvisioningEvents.AnyAsync(e => e.CallerUserId == _fx.UserId && e.Outcome == AccountProvisioningOutcomes.RefusedScope));
    }

    // ── Limits and alerts ───────────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task The_daily_cap_refuses_new_accounts_but_not_a_replay_and_alerts()
    {
        var alerter = new CapturingAlerter();
        await using var app = App(new ProvisioningOptions { MaxAccountsPerDay = 2, AlertAccountsPerHour = 2, CallsPerMinute = 1000 }, alerter);
        var (parentId, mainKey) = await NewParentAsync(app);
        var firstId = NewExternalId();
        var firstToken = NewToken();

        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, firstId, firstToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, NewExternalId(), NewToken())).StatusCode);
        Assert.Contains(AccountProvisioner.UnusualAlertKey, alerter.Keys);

        var over = await ProvisionAsync(app, mainKey, NewExternalId(), NewToken());
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("PROVISIONING_LIMIT", await CodeAsync(over));
        Assert.True(over.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Contains(AccountProvisioner.LimitAlertKey, alerter.Keys);

        // A retry of what already exists is not a new account.
        Assert.Equal(HttpStatusCode.OK, (await ProvisionAsync(app, mainKey, firstId, firstToken)).StatusCode);
        await using var db = _fx.Postgres.CreateContext();
        Assert.Equal(2, await db.Users.CountAsync(u => u.ParentUserId == parentId));
        Assert.Contains(await EventsAsync(parentId), e => e.Outcome == AccountProvisioningOutcomes.RefusedLimit);
    }

    [DockerFact]
    public async Task Calls_per_minute_are_limited_per_token()
    {
        await using var app = App(new ProvisioningOptions { MaxAccountsPerDay = 0, AlertAccountsPerHour = 0, CallsPerMinute = 2 });
        var (_, mainKey) = await NewParentAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/provisioning/accounts", mainKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, NewExternalId(), NewToken())).StatusCode);
        var third = await ProvisionAsync(app, mainKey, NewExternalId(), NewToken());
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("PROVISIONING_LIMIT", await CodeAsync(third));
    }

    [DockerFact]
    public async Task The_listing_shows_own_accounts_only_and_never_a_token()
    {
        await using var app = App();
        var (_, mainKey) = await NewParentAsync(app);
        var (_, otherKey) = await NewParentAsync(app);
        var mine = NewExternalId();
        var token = NewToken();
        await ProvisionAsync(app, mainKey, mine, token);
        await ProvisionAsync(app, otherKey, NewExternalId(), NewToken());

        var all = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/api/provisioning/accounts", mainKey));
        var accounts = all.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(mine, Assert.Single(accounts).GetProperty("externalId").GetString());
        Assert.True(accounts[0].GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain(token, all.GetRawText());

        var filtered = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/api/provisioning/accounts?externalId=" + NewExternalId(), mainKey));
        Assert.Empty(filtered.GetProperty("accounts").EnumerateArray());
    }

    // ── Inheritance ─────────────────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Enterprise_seal_grant_and_activity_are_inherited_from_the_parent()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var childToken = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, NewExternalId(), childToken)).StatusCode);

        // No credits of its own, still signs: the parent is Enterprise.
        var presign = await SendAsync(app, HttpMethod.Post, "/api/presign", childToken, PresignBody());
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var seal = await SendAsync(app, HttpMethod.Post, "/api/seal", childToken, new { });
        Assert.Equal("SEAL_CERTIFICATE_REQUIRED", await CodeAsync(seal)); // past the grant

        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == parentId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsEnterprise, false).SetProperty(u => u.SealAllowed, false));
        }
        Assert.Equal(HttpStatusCode.PaymentRequired, (await SendAsync(app, HttpMethod.Post, "/api/presign", childToken, PresignBody())).StatusCode);
        Assert.Equal("SEAL_NOT_PERMITTED", await CodeAsync(await SendAsync(app, HttpMethod.Post, "/api/seal", childToken, new { })));

        // A deactivated parent stops a tenant whose token was not seen before.
        var freshToken = NewToken();
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == parentId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsEnterprise, true));
        }
        Assert.Equal(HttpStatusCode.Created, (await ProvisionAsync(app, mainKey, NewExternalId(), freshToken)).StatusCode);
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == parentId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", freshToken)).StatusCode);
    }

    // ── Isolation between tenants ───────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_tenant_key_sees_no_presign_operation_or_template_of_another_tenant()
    {
        await using var app = App();
        var (_, mainKey) = await NewParentAsync(app);
        var tokenA = NewToken();
        var tokenB = NewToken();
        await ProvisionAsync(app, mainKey, NewExternalId(), tokenA);
        var b = await JsonAsync(await ProvisionAsync(app, mainKey, NewExternalId(), tokenB));
        var accountB = b.GetProperty("accountId").GetGuid();

        // B's pending presign: A may not sign it.
        var presign = await JsonAsync(await SendAsync(app, HttpMethod.Post, "/api/presign", tokenB, PresignBody()));
        var presignId = presign.GetProperty("id").GetString()!;
        var sign = await SendAsync(app, HttpMethod.Post, "/api/sign", tokenA, new { id = presignId, signedHash = "00" });
        Assert.Equal(HttpStatusCode.NotFound, sign.StatusCode);
        Assert.Equal("SIGNING_DATA_NOT_FOUND", await CodeAsync(sign));

        // B's completed signature (presign gone, operation kept): A gets neither the replay nor the state.
        var completedPresign = Guid.NewGuid().ToString();
        const string key = "op-shared-key";
        await using (var db = _fx.Postgres.CreateContext())
        {
            db.ApiOperations.Add(new ApiOperation
            {
                UserId = accountB,
                OperationId = key,
                ClientKey = true,
                Operation = "sign",
                PresignId = completedPresign,
                RequestFingerprint = "f",
                State = ApiOperationState.Completed,
                ResultProtected = [1, 2, 3],
                ResultExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }
        var replay = await SendAsync(app, HttpMethod.Post, "/api/sign", tokenA, new { id = completedPresign, signedHash = "00" });
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.DoesNotContain("result", await replay.Content.ReadAsStringAsync());
        var state = await SendAsync(app, HttpMethod.Get, "/api/operations/" + key, tokenA);
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/operations/" + key, tokenB)).StatusCode);

        // B's template: not in A's list, not readable by id.
        var template = await SendAsync(app, HttpMethod.Post, "/api/pdf-template", tokenB, new
        {
            pdfContent = TestHelpers.CreateMinimalPdfBase64(),
            templateName = "secret-of-b",
            fields = new[] { new { fieldName = "F", type = (int)PdfFieldType.Text, rect = new { x = 10, y = 10, width = 100, height = 20 }, page = 1, fontSize = 12 } },
        });
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        var templateId = (await JsonAsync(template)).GetProperty("templateId").GetGuid();
        var list = await SendAsync(app, HttpMethod.Get, "/api/pdf-template", tokenA);
        Assert.DoesNotContain("secret-of-b", await list.Content.ReadAsStringAsync());
        var read = await SendAsync(app, HttpMethod.Get, "/api/pdf-template/" + templateId, tokenA);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("TEMPLATE_NOT_FOUND", await CodeAsync(read));

        // Changing, deleting or filling it is the same 404, and B's template stays as it was.
        var update = await SendAsync(app, HttpMethod.Put, "/api/pdf-template/" + templateId, tokenA, new { templateName = "taken-by-a" });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal("TEMPLATE_NOT_FOUND", await CodeAsync(update));
        var delete = await SendAsync(app, HttpMethod.Delete, "/api/pdf-template/" + templateId, tokenA);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("TEMPLATE_NOT_FOUND", await CodeAsync(delete));
        var fill = await SendAsync(app, HttpMethod.Post, "/api/fill-pdf", tokenA, new
        {
            templateId,
            data = new[] { new { data = new[] { new { fieldName = "F", value = "x" } } } },
        });
        Assert.Equal(HttpStatusCode.NotFound, fill.StatusCode);
        Assert.Equal("TEMPLATE_NOT_FOUND", await CodeAsync(fill));

        var own = await SendAsync(app, HttpMethod.Get, "/api/pdf-template/" + templateId, tokenB);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains("secret-of-b", await own.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task Two_concurrent_calls_with_one_token_for_two_tenants_create_one_account()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var token = NewToken();

        for (var round = 0; round < 5; round++)
        {
            var shared = round == 0 ? token : NewToken();
            var calls = await Task.WhenAll(
                ProvisionAsync(app, mainKey, NewExternalId(), shared),
                ProvisionAsync(app, mainKey, NewExternalId(), shared));
            Assert.Single(calls, c => c.StatusCode == HttpStatusCode.Created);
            Assert.Single(calls, c => c.StatusCode == HttpStatusCode.Conflict);
        }

        await using var db = _fx.Postgres.CreateContext();
        Assert.Equal(5, await db.Users.CountAsync(u => u.ParentUserId == parentId));
        using var scope = app.Services.CreateScope();
        var hash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashToken(ApiAuthService.NormalizeToken(token));
        Assert.Equal(1, await db.ApiTokens.CountAsync(t => t.TokenHash == hash));
    }

    [DockerFact]
    public async Task A_value_shared_by_two_active_tokens_authenticates_nobody()
    {
        await using var app = App();
        var token = NewToken();
        using (var scope = app.Services.CreateScope())
        {
            var hash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashToken(ApiAuthService.NormalizeToken(token));
            await using var db = _fx.Postgres.CreateContext();
            var a = new User { Email = $"a-{Guid.NewGuid():N}@example.com", IsActive = true };
            var b = new User { Email = $"b-{Guid.NewGuid():N}@example.com", IsActive = true };
            db.Users.AddRange(a, b);
            db.ApiTokens.Add(new ApiToken { UserId = a.Id, Label = "a", TokenHash = hash });
            db.ApiTokens.Add(new ApiToken { UserId = b.Id, Label = "b", TokenHash = hash });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", token)).StatusCode);
    }

    [DockerFact]
    public async Task The_pending_presign_cap_is_per_tenant()
    {
        await using var app = _fx.App.WithWebHostBuilder(b => b.UseSetting("Limits:MaxPendingPresignsPerUser", "1")
            .ConfigureTestServices(s =>
            {
                s.RemoveAll<ProvisioningOptions>();
                s.AddSingleton(new ProvisioningOptions { MaxAccountsPerDay = 0, AlertAccountsPerHour = 0, CallsPerMinute = 1000 });
            }));
        var (_, mainKey) = await NewParentAsync(app);
        var tokenA = NewToken();
        var tokenB = NewToken();
        await ProvisionAsync(app, mainKey, NewExternalId(), tokenA);
        await ProvisionAsync(app, mainKey, NewExternalId(), tokenB);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Post, "/api/presign", tokenA, PresignBody())).StatusCode);
        var over = await SendAsync(app, HttpMethod.Post, "/api/presign", tokenA, PresignBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("TOO_MANY_PENDING_PRESIGNS", await CodeAsync(over));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Post, "/api/presign", tokenB, PresignBody())).StatusCode);
    }

    // ── Rotation and deletion ───────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Rotation_adds_the_new_token_then_revoke_others_retires_the_old_one()
    {
        await using var app = App();
        var (_, mainKey) = await NewParentAsync(app);
        var oldToken = NewToken();
        var externalId = NewExternalId();
        await ProvisionAsync(app, mainKey, externalId, oldToken);
        var newToken = NewToken();

        var rotate = await SendAsync(app, HttpMethod.Post, "/api/account/tokens/rotate", oldToken, new { newToken });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var tokenId = (await JsonAsync(rotate)).GetProperty("tokenId").GetGuid();
        // Repeated after a lost answer: the same token, nothing new.
        var again = await SendAsync(app, HttpMethod.Post, "/api/account/tokens/rotate", oldToken, new { newToken });
        Assert.Equal(tokenId, (await JsonAsync(again)).GetProperty("tokenId").GetGuid());

        // Both work until the second step.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", newToken)).StatusCode);
        // Provisioning still recognises the account by either active token.
        Assert.Equal(HttpStatusCode.OK, (await ProvisionAsync(app, mainKey, externalId, newToken)).StatusCode);

        var revoke = await SendAsync(app, HttpMethod.Post, "/api/account/tokens/revoke-others", newToken);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(1, (await JsonAsync(revoke)).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, HttpMethod.Get, "/api/pdf-template", oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Post, "/api/account/tokens/revoke-others", newToken)).StatusCode);

        // A revoked or foreign value is never taken again; a weak one is refused.
        Assert.Equal("TOKEN_IN_USE", await CodeAsync(await SendAsync(app, HttpMethod.Post, "/api/account/tokens/rotate", newToken, new { newToken = oldToken })));
        Assert.Equal("TOKEN_IN_USE", await CodeAsync(await SendAsync(app, HttpMethod.Post, "/api/account/tokens/rotate", newToken, new { newToken = mainKey })));
        Assert.Equal("TOKEN_WEAK", await CodeAsync(await SendAsync(app, HttpMethod.Post, "/api/account/tokens/rotate", newToken, new { newToken = "weak" })));
    }

    [DockerFact]
    public async Task Ordinary_accounts_cannot_use_the_provisioned_account_endpoints()
    {
        await using var app = App();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/account/tokens/rotate"), (HttpMethod.Post, "/api/account/tokens/revoke-others"), (HttpMethod.Delete, "/api/account"),
                 })
        {
            var response = await SendAsync(app, method, path, _fx.ApiToken, method == HttpMethod.Delete ? null : new { newToken = NewToken() });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("TOKEN_SCOPE", await CodeAsync(response));
        }
        await using var db = _fx.Postgres.CreateContext();
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == _fx.UserId)).ErasedAt);
    }

    [DockerFact]
    public async Task Delete_erases_the_account_its_tokens_go_and_the_tenant_can_be_provisioned_again()
    {
        await using var app = App();
        var (parentId, mainKey) = await NewParentAsync(app);
        var externalId = NewExternalId();
        var token = NewToken();
        var created = await JsonAsync(await ProvisionAsync(app, mainKey, externalId, token));
        var accountId = created.GetProperty("accountId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, HttpMethod.Post, "/api/presign", token, PresignBody())).StatusCode);

        var delete = await SendAsync(app, HttpMethod.Delete, "/api/account", token);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal("erased", (await JsonAsync(delete)).GetProperty("status").GetString());
        // Its tokens are gone: a repeat is 401, which the integrator takes as done.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, HttpMethod.Delete, "/api/account", token)).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            var tombstone = await db.Users.AsNoTracking().SingleAsync(u => u.Id == accountId);
            Assert.NotNull(tombstone.ErasedAt);
            Assert.False(await db.SigningData.AnyAsync(s => s.UserId == accountId));
            Assert.False(await db.ApiTokens.AnyAsync(t => t.UserId == accountId));
        }

        // The tenant reopens: the same external id, a new account and a new token.
        var newToken = NewToken();
        var again = await ProvisionAsync(app, mainKey, externalId, newToken);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(accountId, (await JsonAsync(again)).GetProperty("accountId").GetGuid());

        var list = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/api/provisioning/accounts?externalId=" + externalId, mainKey));
        var rows = list.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.GetProperty("erasedAt").ValueKind != JsonValueKind.Null && !r.GetProperty("isActive").GetBoolean());

        // The parent with a live child is not erasable; the admin sees why.
        using var scope = app.Services.CreateScope();
        var erasure = scope.ServiceProvider.GetRequiredService<DotNetSigningServer.Services.Erasure.AccountErasureService>();
        var refused = await erasure.DeleteAccountAsync(parentId, CancellationToken.None);
        Assert.Equal(DotNetSigningServer.Services.Erasure.AccountErasureRefusal.HasLiveChildren, refused.Refusal);
    }
}
