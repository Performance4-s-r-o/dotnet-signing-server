using System.Net;
using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// The whole app (<see cref="WebApplicationFactory{TEntryPoint}"/>) on a throwaway PostgreSQL,
/// with the Consents module Off or On. The backoffice service is never reachable
/// (<c>https://127.0.0.1:9</c>), e-mail is a no-op and background services are removed, so
/// nothing leaves the test.
/// </summary>
internal sealed class SignUpAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _consentsMode;

    public SignUpAppFactory(string connectionString, string consentsMode)
    {
        _connectionString = connectionString;
        _consentsMode = consentsMode;
        ClientOptions.BaseAddress = new Uri("https://localhost");
        ClientOptions.AllowAutoRedirect = false;
    }

    private sealed class NoEmail : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that environment's settings start a local database and hold real keys.
        builder.UseEnvironment("Testing");
        builder.UseSetting("UseLocalDb", "false");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEmailSender>();
            services.AddScoped<IEmailSender, NoEmail>();
            services.PostConfigure<P4BackofficeProductOptions>(o =>
            {
                // The default build has no SDK and forces every module Off; the test switches Consents itself.
                o.DisabledReason = BackofficeDisabledReason.None;
                o.Modules.Consents = _consentsMode;
                o.BaseUrl = "https://127.0.0.1:9";
                o.SecretKey = "p4sk_test_integration";
            });
        });
    }
}

/// <summary>One PostgreSQL for the class; a private data-protection key ring outside the repository.</summary>
public sealed class SignUpPostgresFixture : IAsyncLifetime
{
    public OutboxPostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
    }

    public Task DisposeAsync() => Postgres.DisposeAsync();
}

[Trait("Category", "Db")]
public class SignUpIntegrationTests : IClassFixture<SignUpPostgresFixture>
{
    private readonly SignUpPostgresFixture _fixture;

    public SignUpIntegrationTests(SignUpPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly Regex AntiforgeryToken = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static async Task<(string Token, string Html)> OpenFormAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Account/SignUp");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        return (AntiforgeryToken.Match(html).Groups[1].Value, html);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string email, bool accept, IDictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Email"] = email,
            ["Password"] = "correct horse battery",
            ["ConfirmPassword"] = "correct horse battery",
        };
        if (accept) form["AcceptTerms"] = "true";
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) form[k] = v;
        return client.PostAsync("/Account/SignUp", new FormUrlEncodedContent(form));
    }

    private async Task<T> DbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        await using var db = _fixture.Postgres.CreateContext();
        return await action(db);
    }

    private static string Email() => $"u{Guid.NewGuid():N}@example.com";

    [DockerFact]
    public async Task On_FormShowsVersionsAndAnUncheckedBox()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "On");
        var (_, html) = await OpenFormAsync(app.CreateClient());

        Assert.Contains("name=\"ShownVersions[terms]\" value=\"1\"", html);
        Assert.Contains("name=\"ShownVersions[dpa]\" value=\"1\"", html);
        Assert.Contains("name=\"ShownVersions[privacy]\" value=\"1\"", html);
        var checkbox = Regex.Match(html, "<input[^>]*name=\"AcceptTerms\"[^>]*type=\"checkbox\"[^>]*>|<input[^>]*type=\"checkbox\"[^>]*name=\"AcceptTerms\"[^>]*>").Value;
        Assert.NotEmpty(checkbox);
        Assert.DoesNotContain("checked", checkbox);
        Assert.Contains("/Legal/TermsOfService", html);
        Assert.Contains("/Legal/DataProcessingAgreement", html);
        Assert.Contains("/Legal/PrivacyPolicy", html);
    }

    [DockerFact]
    public async Task On_WithoutTheCheckbox_IsRefusedEvenWhenHtmlRequiredIsBypassed()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "On");
        var client = app.CreateClient();
        var (token, _) = await OpenFormAsync(client);
        var email = Email();

        var response = await PostAsync(client, token, email, accept: false,
            new Dictionary<string, string> { ["ShownVersions[terms]"] = "1", ["ShownVersions[dpa]"] = "1", ["ShownVersions[privacy]"] = "1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await DbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [DockerFact]
    public async Task On_Signup_WritesUserThreeRecordsAndOneOutboxBatch()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "On");
        var client = app.CreateClient();
        var (token, _) = await OpenFormAsync(client);
        var email = Email();

        var response = await PostAsync(client, token, email, accept: true,
            new Dictionary<string, string> { ["ShownVersions[terms]"] = "1", ["ShownVersions[dpa]"] = "1", ["ShownVersions[privacy]"] = "1" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Verify", response.Headers.Location?.OriginalString);
        var user = await DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
        var records = await DbAsync(db => db.ConsentRecords.AsNoTracking().Where(r => r.UserId == user.Id).ToListAsync());
        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r is { Document: "terms", Action: ConsentActions.Granted, Version: 1 });
        Assert.Contains(records, r => r is { Document: "dpa", Action: ConsentActions.Granted, Version: 1 });
        Assert.Contains(records, r => r is { Document: "privacy", Action: ConsentActions.Acknowledged, Version: 1 });
        var outboxId = Assert.Single(records.Select(r => r.OutboxItemId).Distinct());
        var item = await DbAsync(db => db.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == outboxId));
        Assert.Equal("consent", item.Kind);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal($"dotnet:user:{user.Id}", item.SubjectRef);

        // The same address again: the same answer, and no consent or outbox item is written for it
        // (no database side effect that would reveal the account exists).
        var recordsBefore = await DbAsync(db => db.ConsentRecords.CountAsync());
        var itemsBefore = await DbAsync(db => db.BackofficeOutboxItems.CountAsync());
        (token, _) = await OpenFormAsync(client);
        var again = await PostAsync(client, token, email, accept: true,
            new Dictionary<string, string> { ["ShownVersions[terms]"] = "1", ["ShownVersions[dpa]"] = "1", ["ShownVersions[privacy]"] = "1" });
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal("/Account/Verify", again.Headers.Location?.OriginalString);
        Assert.Equal(3, await DbAsync(db => db.ConsentRecords.CountAsync(r => r.UserId == user.Id)));
        Assert.Equal(recordsBefore, await DbAsync(db => db.ConsentRecords.CountAsync()));
        Assert.Equal(itemsBefore, await DbAsync(db => db.BackofficeOutboxItems.CountAsync()));
    }

    [DockerFact]
    public async Task On_FormOpenDuringAPublication_PassesForTenMinutes_ThenIsOutdated()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "On");
        var client = app.CreateClient();

        // terms v2 came into force 5 minutes ago; the form still shows v1.
        await SetDocsMetaAsync(termsSince: DateTimeOffset.UtcNow.AddMinutes(-5));
        try
        {
            var (token, html) = await OpenFormAsync(client);
            Assert.Contains("name=\"ShownVersions[terms]\" value=\"2\"", html);
            var within = Email();
            var ok = await PostAsync(client, token, within, accept: true,
                new Dictionary<string, string> { ["ShownVersions[terms]"] = "1", ["ShownVersions[dpa]"] = "1", ["ShownVersions[privacy]"] = "1" });
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            var userId = await DbAsync(db => db.Users.Where(u => u.Email == within).Select(u => u.Id).SingleAsync());
            Assert.Equal(1, await DbAsync(db => db.ConsentRecords.Where(r => r.UserId == userId && r.Document == "terms").Select(r => r.Version).SingleAsync()));

            // 20 minutes after the publication the old version is refused.
            await SetDocsMetaAsync(termsSince: DateTimeOffset.UtcNow.AddMinutes(-20));
            var late = Email();
            var refused = await PostAsync(client, token, late, accept: true,
                new Dictionary<string, string> { ["ShownVersions[terms]"] = "1", ["ShownVersions[dpa]"] = "1", ["ShownVersions[privacy]"] = "1" });
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains("name=\"ShownVersions[terms]\" value=\"2\"", body);
            Assert.False(await DbAsync(db => db.Users.AnyAsync(u => u.Email == late)));
        }
        finally
        {
            await DbAsync(async db => await db.BackofficeStates.Where(s => s.Key == BackofficeStateKeys.DocsMeta).ExecuteDeleteAsync());
        }
    }

    [DockerFact]
    public async Task Off_Signup_RecordsLocallyWithoutOutbox()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "Off");
        var client = app.CreateClient();
        var (token, html) = await OpenFormAsync(client);
        Assert.DoesNotContain("ShownVersions[", html);
        var email = Email();

        var response = await PostAsync(client, token, email, accept: true);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var userId = await DbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        var records = await DbAsync(db => db.ConsentRecords.AsNoTracking().Where(r => r.UserId == userId).ToListAsync());
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Null(r.OutboxItemId));
        Assert.False(await DbAsync(db => db.BackofficeOutboxItems.AnyAsync(i => i.SubjectRef == $"dotnet:user:{userId}")));
    }

    [DockerFact]
    public async Task Off_WithoutTheCheckbox_IsRefused()
    {
        using var app = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "Off");
        var client = app.CreateClient();
        var (token, _) = await OpenFormAsync(client);
        var email = Email();

        var response = await PostAsync(client, token, email, accept: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await DbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    private Task<int> SetDocsMetaAsync(DateTimeOffset termsSince) =>
        DbAsync(async db =>
        {
            var meta = new DocumentsMeta(DateTimeOffset.UtcNow, new()
            {
                ["terms"] = new DocumentMeta(true, 2, 2, null, termsSince, "Clearer refund rules."),
                ["dpa"] = new DocumentMeta(true, 1, 1, null, termsSince.AddDays(-100)),
                ["privacy"] = new DocumentMeta(true, 1, 1, null, termsSince.AddDays(-100)),
            });
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta, meta.ToJson(), DateTimeOffset.UtcNow);
            return 0;
        });
}
