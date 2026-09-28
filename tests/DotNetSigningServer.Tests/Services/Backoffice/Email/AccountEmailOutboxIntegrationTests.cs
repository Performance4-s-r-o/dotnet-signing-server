using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using DotNetSigningServer.Tests.Services.Consents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>
/// Sign-up, sign-in and password reset with the Email module's outbox sender, on the whole app
/// and a throwaway PostgreSQL. No dispatcher runs, so nothing leaves the test: the e-mail must
/// be in the outbox, committed with the account change, encrypted.
/// </summary>
[Trait("Category", "Db")]
public class AccountEmailOutboxIntegrationTests : IClassFixture<SignUpPostgresFixture>
{
    private static readonly Regex AntiforgeryToken = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private readonly SignUpPostgresFixture _fixture;

    public AccountEmailOutboxIntegrationTests(SignUpPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private SignUpAppFactory App() => new(_fixture.Postgres.ConnectionString, "Off", services =>
    {
        services.RemoveAll<IEmailSender>();
        services.AddScoped<BackofficeOutboxEmailSender>();
        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<BackofficeOutboxEmailSender>());
    });

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        return AntiforgeryToken.Match(html).Groups[1].Value;
    }

    private async Task<T> DbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        await using var db = _fixture.Postgres.CreateContext();
        return await action(db);
    }

    private Task<List<BackofficeOutboxItem>> EmailItemsAsync(string subjectRef) =>
        DbAsync(db => db.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.Kind == BackofficeOutboxEmailSender.OutboxKind && i.SubjectRef == subjectRef)
            .ToListAsync());

    private static string Email() => $"u{Guid.NewGuid():N}@example.com";

    [DockerFact]
    public async Task SignUp_QueuesTheVerificationWithTheUser()
    {
        using var app = App();
        var client = app.CreateClient();
        var email = Email();

        var response = await client.PostAsync("/Account/SignUp", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(client, "/Account/SignUp"),
            ["Email"] = email,
            ["Password"] = "correct horse battery",
            ["ConfirmPassword"] = "correct horse battery",
            ["AcceptTerms"] = "true",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var user = await DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
        Assert.False(string.IsNullOrEmpty(user.Locale));
        var item = Assert.Single(await EmailItemsAsync($"user:{user.Id}"));
        Assert.True(item.Critical);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.DoesNotContain("Verify?token=", item.PayloadProtected);
        Assert.DoesNotContain(email, item.PayloadProtected);
    }

    [DockerFact]
    public async Task SignIn_QueuesTheCodeWithItsHash_AndRemembersTheLanguage()
    {
        using var app = App();
        var email = Email();
        Guid userId;
        using (var scope = app.Services.CreateScope())
        {
            var (hash, salt, iterations) = scope.ServiceProvider.GetRequiredService<IAuthService>().HashPassword("correct horse battery");
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new User
            {
                Email = email,
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations,
                EmailVerified = true,
                IsActive = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = app.CreateClient();
        var response = await client.PostAsync("/Account/SignIn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(client, "/Account/SignIn"),
            ["Email"] = email,
            ["Password"] = "correct horse battery",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/TwoFactor", response.Headers.Location?.OriginalString);
        var stored = await DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));
        Assert.NotNull(stored.EmailOtpCode);
        Assert.False(string.IsNullOrEmpty(stored.Locale));
        var item = Assert.Single(await EmailItemsAsync($"user:{userId}"));
        Assert.True(item.Critical);

        // The code is only in the encrypted payload.
        var json = app.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(item.PayloadProtected!);
        var payload = JsonSerializer.Deserialize<EmailRawPayload>(json, BackofficeOutbox.PayloadJson)!;
        var code = Regex.Match(payload.Html, @">(\d{6})<").Groups[1].Value;
        Assert.Equal(6, code.Length);
        Assert.Equal(SecureTokens.Hash(code), stored.EmailOtpCode);
        Assert.DoesNotContain(code, item.PayloadProtected);
        Assert.Equal(EmailTemplateId.TwoFactorCode, payload.Tags["template"]);
        Assert.Equal(userId.ToString(), payload.Tags["user_id"]);
        Assert.Equal(stored.Locale, payload.Tags["locale"]);
    }

    [DockerFact]
    public async Task ForgotPassword_QueuesTheResetLinkWithTheToken()
    {
        using var app = App();
        var email = Email();
        Guid userId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new User { Email = email, EmailVerified = true, IsActive = true, PasswordHash = [1], PasswordSalt = [1] };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = app.CreateClient();
        var response = await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(client, "/Account/ForgotPassword"),
            ["Email"] = email,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));
        Assert.NotNull(stored.PasswordResetToken);
        var item = Assert.Single(await EmailItemsAsync($"user:{userId}"));
        Assert.True(item.Critical);
        Assert.DoesNotContain("ResetPassword?token=", item.PayloadProtected);
    }
}
