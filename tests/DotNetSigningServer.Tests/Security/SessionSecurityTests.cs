using System.Security.Claims;
using DotNetSigningServer.Filters;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Security;

/// <summary>#80: the pure rules — cookie revalidation, browser token scope, re-auth window, checkout marker, audit.</summary>
public class SessionSecurityTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string StampText = Stamp.Ticks.ToString();

    [Fact]
    public void Cookie_ActiveUserWithSameStampAndRole_IsAccepted() =>
        Assert.Equal(CookieSessionValidator.Decision.Accept, CookieSessionValidator.Decide(StampText, false, Stamp, true, false));

    [Fact]
    public void Cookie_DeactivatedUser_IsRejected_EvenWithTheSameStamp() =>
        // A deactivation written without touching UpdatedAt (SQL, another write path).
        Assert.Equal(CookieSessionValidator.Decision.Reject, CookieSessionValidator.Decide(StampText, false, Stamp, false, false));

    [Fact]
    public void Cookie_DeletedUserOrOtherStamp_IsRejected()
    {
        Assert.Equal(CookieSessionValidator.Decision.Reject, CookieSessionValidator.Decide(StampText, false, null, null, null));
        Assert.Equal(CookieSessionValidator.Decision.Reject, CookieSessionValidator.Decide(StampText, false, Stamp.AddTicks(10), true, false));
    }

    [Theory]
    [InlineData(true, false)]  // demoted: the Admin claim from sign-in must go
    [InlineData(false, true)]  // promoted: the role follows the database too
    public void Cookie_RoleDifferentFromDatabase_IsRefreshed(bool claimsAdmin, bool isAdmin) =>
        Assert.Equal(CookieSessionValidator.Decision.RefreshRoles, CookieSessionValidator.Decide(StampText, claimsAdmin, Stamp, true, isAdmin));

    [Fact]
    public async Task SaveChanges_ChangeOfIsActiveOrIsAdmin_MovesTheStamp_WhoeverWritesIt()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        var user = new User { Email = "a@example.cz", IsAdmin = true, UpdatedAt = Stamp };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        user.IsAdmin = false; // an admin's write path that forgets UpdatedAt
        await db.SaveChangesAsync();
        Assert.True(user.UpdatedAt > Stamp);

        var second = user.UpdatedAt;
        user.IsActive = false;
        await db.SaveChangesAsync();
        Assert.True(user.UpdatedAt > second);

        var third = user.UpdatedAt;
        user.CreditsRemaining += 5; // anything else leaves the session alone
        await db.SaveChangesAsync();
        Assert.Equal(third, user.UpdatedAt);
    }

    [Theory]
    [InlineData("POST", "/api/presign", true)]
    [InlineData("POST", "/api/sign", true)]
    [InlineData("GET", "/api/operations/abc", true)]
    [InlineData("GET", "/api/capabilities", true)]
    [InlineData("POST", "/api/sign-pfx", false)]
    [InlineData("POST", "/api/seal", false)]
    [InlineData("GET", "/api/pdf-template", false)]
    [InlineData("POST", "/api/pdf-template", false)]
    [InlineData("DELETE", "/api/pdf-template/1", false)]
    [InlineData("POST", "/api/fill-pdf", false)]
    [InlineData("GET", "/api/presign", false)]
    public void BrowserToken_Scope(string method, string path, bool allowed) =>
        Assert.Equal(allowed, BrowserTokenScope.IsAllowed(method, new PathString(path)));

    [Fact]
    public void Reauth_FreshOnlyWithinTheWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(15);
        Assert.True(RequireRecentAuthAttribute.IsFresh(now.AddMinutes(-14), now, window));
        Assert.False(RequireRecentAuthAttribute.IsFresh(now.AddMinutes(-16), now, window));
        Assert.False(RequireRecentAuthAttribute.IsFresh(null, now, window)); // sessions from before #80
        Assert.False(RequireRecentAuthAttribute.IsFresh(now.AddHours(1), now, window)); // from the future
    }

    [Fact]
    public void Reauth_WindowIsConfigurable_DefaultFifteen()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), new DotNetSigningServer.Options.SessionSecurityOptions().ReauthWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), new DotNetSigningServer.Options.SessionSecurityOptions { ReauthMinutes = 5 }.ReauthWindow);
    }

    [Fact]
    public void Reauth_SignInPrincipalCarriesAuthTime()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var principal = SessionClaims.CookiePrincipal(new User { Email = "a@example.cz", IsAdmin = true }, at);
        Assert.Equal(at, SessionClaims.AuthTimeOf(principal));
        Assert.True(principal.IsInRole(SessionClaims.AdminRole));
    }

    [Fact]
    public void Checkout_WithoutTheProductMarker_BelongsToNobody()
    {
        var userId = Guid.NewGuid();
        var foreign = new CheckoutSessionInfo("cs_1", "payment", "complete", "paid",
            new Dictionary<string, string> { ["userId"] = userId.ToString() }, null, null);
        Assert.False(BillingCustomerRef.CheckoutBelongsTo(foreign, userId, null));

        var ours = new CheckoutSessionInfo("cs_2", "payment", "complete", "paid",
            new Dictionary<string, string> { ["userId"] = userId.ToString(), [BillingCustomerRef.ProductMarkerKey] = BillingCustomerRef.ProductMarker }, null, null);
        Assert.True(BillingCustomerRef.CheckoutBelongsTo(ours, userId, null));
    }

    [Theory]
    // created,              graceUntil,            names user, expected
    [InlineData("2026-10-20T10:00:00Z", "2026-10-20T12:00:00Z", true, true)]   // open before the deploy: accepted
    [InlineData("2026-10-20T13:00:00Z", "2026-10-20T12:00:00Z", true, false)]  // created after the grace
    [InlineData("2026-10-20T10:00:00Z", null, true, false)]                    // no grace configured = strict
    [InlineData(null, "2026-10-20T12:00:00Z", true, false)]                    // unknown age never passes
    [InlineData("2026-10-20T10:00:00Z", "2026-10-20T12:00:00Z", false, false)] // the grace never skips the userId check
    public void Checkout_WithoutMarker_PassesOnlyInsideTheGrace(string? created, string? graceUntil, bool namesUser, bool expected)
    {
        var userId = Guid.NewGuid();
        var session = new CheckoutSessionInfo("cs_old", "payment", "complete", "paid",
            new Dictionary<string, string> { ["userId"] = (namesUser ? userId : Guid.NewGuid()).ToString() },
            null, null, created is null ? null : DateTimeOffset.Parse(created));
        var grace = graceUntil is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(graceUntil);

        Assert.Equal(expected, BillingCustomerRef.CheckoutBelongsTo(session, userId, null, grace));
    }

    [Fact]
    public void Checkout_InsideTheGrace_StillNeedsTheUsersCustomer()
    {
        var userId = Guid.NewGuid();
        var session = new CheckoutSessionInfo("cs_old", "payment", "complete", "paid",
            new Dictionary<string, string> { ["userId"] = userId.ToString() },
            "cus_other", null, DateTimeOffset.Parse("2026-10-20T10:00:00Z"));
        Assert.False(BillingCustomerRef.CheckoutBelongsTo(session, userId, "cus_mine", DateTimeOffset.Parse("2026-10-20T12:00:00Z")));
    }

    [Fact]
    public async Task AdminAudit_RecordsActorTargetAndChange_AndIsAppendOnly()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        var actorId = Guid.NewGuid();
        var actor = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
            new Claim(ClaimTypes.Name, "admin@example.cz"),
        }, "Cookies"));
        var target = Guid.NewGuid();

        AdminAudit.Record(db, actor, target, AdminAudit.Actions.SealAllowed, new { from = false, to = true });
        await db.SaveChangesAsync();

        var entry = await db.AdminAuditEntries.SingleAsync();
        Assert.Equal(actorId, entry.ActorUserId);
        Assert.Equal("admin@example.cz", entry.ActorEmail);
        Assert.Equal(target, entry.TargetUserId);
        Assert.Equal("user.seal_allowed", entry.Action);
        Assert.Contains("\"to\":true", entry.Change);

        entry.Change = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.AdminAuditEntries.Remove(await db.AdminAuditEntries.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
