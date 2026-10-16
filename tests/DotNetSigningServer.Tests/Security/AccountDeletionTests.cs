using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Filters;
using DotNetSigningServer.Models;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services.Erasure;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// Deleting the account through the web UI (#217): a password or code confirmed within the last
/// 10 minutes (stricter than <c>Auth:ReauthMinutes</c>), antiforgery, a ticked confirmation —
/// and the texts in EN and CS.
/// </summary>
[Trait("Category", "Db")]
public class AccountDeletionTests : IClassFixture<WebUiAuthFixture>
{
    private readonly WebUiAuthFixture _fx;

    public AccountDeletionTests(WebUiAuthFixture fx)
    {
        _fx = fx;
    }

    private async Task<Guid> NewUserAsync()
    {
        await using var db = _fx.Postgres.CreateContext();
        var user = new User { Email = $"del{Guid.NewGuid():N}@example.com", EmailVerified = true, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>GET the page (or the re-auth form it falls back to) for the token, then POST.</summary>
    private async Task<(HttpResponseMessage Page, HttpResponseMessage Post)> DeleteAsync(Guid userId, DateTimeOffset? authTime, bool confirm, bool json = true)
    {
        var client = _fx.App.CreateClient();
        var session = _fx.CookieHeaderFor(userId, authTime: authTime);
        var page = new HttpRequestMessage(HttpMethod.Get, AccountController.DeleteAccountPath);
        page.Headers.Add("Cookie", session);
        var pageResponse = await client.SendAsync(page);
        var html = await pageResponse.Content.ReadAsStringAsync();
        var antiforgeryCookie = pageResponse.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0]).First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));

        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AntiforgeryToken(html) };
        if (confirm) form["confirm"] = "true";
        var post = new HttpRequestMessage(HttpMethod.Post, AccountController.DeleteAccountPath) { Content = new FormUrlEncodedContent(form) };
        post.Headers.Add("Cookie", $"{session}; {antiforgeryCookie}");
        if (json) post.Headers.Accept.ParseAdd("application/json");
        return (pageResponse, await client.SendAsync(post));
    }

    private async Task<User> UserAsync(Guid id)
    {
        await using var db = _fx.Postgres.CreateContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    [DockerFact]
    public async Task Without_a_reauth_in_the_last_10_minutes_nothing_is_deleted()
    {
        var id = await NewUserAsync();

        // 11 minutes is inside the general 15-minute window but not the 10 minutes of this action.
        var (page, post) = await DeleteAsync(id, DateTimeOffset.UtcNow.AddMinutes(-11), confirm: true);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains(RequireRecentAuthAttribute.ErrorCode, await post.Content.ReadAsStringAsync());

        var (_, none) = await DeleteAsync(id, authTime: null, confirm: true); // sessions from before #80
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);

        var user = await UserAsync(id);
        Assert.True(user.IsActive);
        Assert.Null(user.ErasedAt);
    }

    [DockerFact]
    public async Task With_a_fresh_reauth_and_the_confirmation_the_account_is_erased_and_signed_out()
    {
        var id = await NewUserAsync();
        var email = (await UserAsync(id)).Email;

        var (page, unconfirmed) = await DeleteAsync(id, DateTimeOffset.UtcNow.AddMinutes(-2), confirm: false);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("ACCOUNT_DELETE_NOT_CONFIRMED", await unconfirmed.Content.ReadAsStringAsync());
        Assert.Null((await UserAsync(id)).ErasedAt);

        var (_, deleted) = await DeleteAsync(id, DateTimeOffset.UtcNow.AddMinutes(-2), confirm: true);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains("\"deleted\"", await deleted.Content.ReadAsStringAsync());

        var user = await UserAsync(id);
        Assert.NotNull(user.ErasedAt);
        Assert.False(user.IsActive);
        Assert.NotEqual(email, user.Email);
        Assert.EndsWith(AccountErasure.TombstoneDomain, user.Email);

        // The old session no longer opens the account (IsActive moved the stamp).
        var client = _fx.App.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Settings");
        request.Headers.Add("Cookie", _fx.CookieHeaderFor(id, authTime: DateTimeOffset.UtcNow));
        var after = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    [DockerFact]
    public async Task Without_the_antiforgery_token_the_post_is_refused()
    {
        var id = await NewUserAsync();
        var client = _fx.App.CreateClient();
        var post = new HttpRequestMessage(HttpMethod.Post, AccountController.DeleteAccountPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "true" }),
        };
        post.Headers.Add("Cookie", _fx.CookieHeaderFor(id, authTime: DateTimeOffset.UtcNow));
        var response = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await UserAsync(id)).ErasedAt);
    }

    public static readonly TheoryData<string> Keys = new()
    {
        "AccountDeleteTitle", "AccountDeleteSettingsHint", "AccountDeleteOpenButton", "AccountDeleteIntro",
        "AccountDeleteWhatGoes", "AccountDeleteCredits", "AccountDeleteBilling", "AccountDeleteWhatStays",
        "AccountDeleteElsewhere", "AccountDeleteConfirmLabel", "AccountDeleteButton", "AccountDeleteConfirmRequired",
        "AccountDeleteFailed", "AccountDeleteBillingFailed", "AccountDeleted",
    };

    [DockerTheory]
    [MemberData(nameof(Keys))]
    public void Every_text_exists_in_English_and_Czech(string key)
    {
        var localizer = _fx.App.Services.GetRequiredService<IStringLocalizer<SharedStrings>>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var en = localizer[key];
            CultureInfo.CurrentUICulture = new CultureInfo("cs");
            var cs = localizer[key];
            Assert.False(en.ResourceNotFound);
            Assert.False(cs.ResourceNotFound);
            Assert.NotEqual(en.Value, cs.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
