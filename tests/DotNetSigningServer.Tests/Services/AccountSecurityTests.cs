using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#80: 2FA bound to its sign-in, codes void after N failures, reset and forgot-password.</summary>
public class AccountSecurityTests
{
    private static async Task<(DotNetSigningServer.Data.ApplicationDbContext Db, User User)> SeedAsync(string otp, string? challenge)
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.cz",
            EmailVerified = true,
            IsActive = true,
            EmailOtpCode = SecureTokens.Hash(otp),
            EmailOtpExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            LoginChallengeHash = challenge == null ? null : SecureTokens.Hash(challenge),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    private static AccountController WithChallenge(DotNetSigningServer.Data.ApplicationDbContext db, string? challenge)
    {
        var controller = SignInRateLimitTests.Controller(db);
        if (challenge != null)
        {
            controller.HttpContext.Request.Headers.Cookie = $"{AccountController.LoginChallengeCookie}={challenge}";
        }
        return controller;
    }

    [Fact]
    public async Task TwoFactor_WithoutTheChallengeOfTheSignIn_IsRefused_EvenWithTheRightCode()
    {
        var (db, user) = await SeedAsync("123456", challenge: "the-real-challenge");

        // No cookie: the address in a form is no longer an input at all.
        var noCookie = WithChallenge(db, null);
        await noCookie.TwoFactorPost("123456");
        Assert.Equal("VerificationCodeInvalid", noCookie.TempData["Error"]);

        // Somebody else's challenge.
        var otherCookie = WithChallenge(db, "another-browsers-challenge");
        await otherCookie.TwoFactorPost("123456");
        Assert.Equal("VerificationCodeInvalid", otherCookie.TempData["Error"]);

        await db.Entry(user).ReloadAsync();
        Assert.NotNull(user.EmailOtpCode); // the owner's code is untouched
    }

    [Fact]
    public async Task TwoFactor_WrongCodes_VoidTheCodeAtTheLimit()
    {
        var challenge = Guid.NewGuid().ToString("N");
        var (db, user) = await SeedAsync("123456", challenge);
        var limit = new DotNetSigningServer.Options.SessionSecurityOptions().OtpMaxFailedAttempts;

        for (var i = 1; i < limit; i++)
        {
            var c = WithChallenge(db, challenge);
            await c.TwoFactorPost("000000");
            Assert.Equal("VerificationCodeInvalid", c.TempData["Error"]);
        }
        await db.Entry(user).ReloadAsync();
        Assert.Equal(limit - 1, user.EmailOtpFailedAttempts);

        var last = WithChallenge(db, challenge);
        await last.TwoFactorPost("000000");
        Assert.Equal("VerificationCodeVoided", last.TempData["Error"]);

        await db.Entry(user).ReloadAsync();
        Assert.Null(user.EmailOtpCode);
        Assert.Null(user.LoginChallengeHash);

        // The right code no longer helps (refused as void, or by the attempt window).
        var after = WithChallenge(db, challenge);
        var result = await after.TwoFactorPost("123456");
        Assert.Contains(after.TempData["Error"] as string, new[] { "VerificationCodeInvalid", "TooManyVerificationAttempts" });
        Assert.Equal("SignIn", Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public async Task TwoFactor_ReauthCode_CannotBeUsedToSignIn()
    {
        // A code sent for re-auth has no challenge; no cookie can match it.
        var (db, _) = await SeedAsync("123456", challenge: null);
        var c = WithChallenge(db, "guess");
        await c.TwoFactorPost("123456");
        Assert.Equal("VerificationCodeInvalid", c.TempData["Error"]);
    }

    [Fact]
    public async Task SignIn_DoesNotResetTheCountOfWrongCodes()
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        var auth = new AuthService();
        var (hash, salt, iterations) = auth.HashPassword("Corr3ct-horse!");
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.cz", EmailVerified = true, IsActive = true,
            PasswordHash = hash, PasswordSalt = salt, PasswordIterations = iterations,
            EmailOtpFailedAttempts = 4,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // A password holder signing in again gets a new code, not a new set of guesses.
        var c = SignInRateLimitTests.Controller(db, ip: "203.0.113.50");
        await c.SignIn(new SignInViewModel { Email = user.Email, Password = "Corr3ct-horse!" });

        await db.Entry(user).ReloadAsync();
        Assert.NotNull(user.EmailOtpCode);
        Assert.Equal(4, user.EmailOtpFailedAttempts);

        // One more wrong code voids it at once (limit 5).
        var challenge = Guid.NewGuid().ToString("N");
        user.LoginChallengeHash = SecureTokens.Hash(challenge);
        await db.SaveChangesAsync();
        var wrong = WithChallenge(db, challenge);
        await wrong.TwoFactorPost("000000");
        Assert.Equal("VerificationCodeVoided", wrong.TempData["Error"]);
        await db.Entry(user).ReloadAsync();
        Assert.Null(user.EmailOtpCode);
        Assert.Equal(5, user.EmailOtpFailedAttempts); // voiding keeps the count too
    }

    [Fact]
    public async Task TwoFactor_NewChallenges_HitTheLimitPerAccount()
    {
        var (db, user) = await SeedAsync("123456", null);
        var security = new DotNetSigningServer.Options.SessionSecurityOptions { AccountMaxAttempts = 3, OtpMaxFailedAttempts = 100 };

        for (var i = 0; i < 4; i++)
        {
            // Every round a fresh sign-in: new challenge, new code.
            var challenge = Guid.NewGuid().ToString("N");
            user.LoginChallengeHash = SecureTokens.Hash(challenge);
            user.EmailOtpCode = SecureTokens.Hash("123456");
            user.EmailOtpExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            await db.SaveChangesAsync();

            var c = SignInRateLimitTests.Controller(db, security: security);
            c.HttpContext.Request.Headers.Cookie = $"{AccountController.LoginChallengeCookie}={challenge}";
            await c.TwoFactorPost("000000");
            Assert.Equal(i < 3 ? "VerificationCodeInvalid" : "TooManyVerificationAttempts", c.TempData["Error"]);
        }
    }

    [Fact]
    public async Task ForgotPassword_DoesNotMoveTheSessionStamp()
    {
        var (db, user) = await SeedAsync("123456", null);
        var stamp = user.UpdatedAt;

        var c = SignInRateLimitTests.Controller(db, app: new DotNetSigningServer.Options.AppOptions { FqdnServerName = "https://sign.example.test" });
        await c.ForgotPassword(new ForgotPasswordViewModel { Email = user.Email });

        await db.Entry(user).ReloadAsync();
        Assert.NotNull(user.PasswordResetToken); // the reset itself was issued
        Assert.Equal(stamp, user.UpdatedAt);     // and nobody was signed out
    }

    [Fact]
    public async Task ResetPassword_RevokesBrowserTokensAndTheWaitingCode_ButKeepsServerTokens()
    {
        var (db, user) = await SeedAsync("123456", "challenge");
        var resetToken = SecureTokens.Generate();
        user.PasswordResetToken = SecureTokens.Hash(resetToken);
        user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var browser = new ApiToken { UserId = user.Id, Label = "web", TokenHash = new byte[] { 1 }, IsBrowserToken = true };
        var server = new ApiToken { UserId = user.Id, Label = "portal", TokenHash = new byte[] { 2 }, IsBrowserToken = false };
        db.ApiTokens.AddRange(browser, server);
        await db.SaveChangesAsync();

        var c = SignInRateLimitTests.Controller(db);
        await c.ResetPassword(new ResetPasswordViewModel { Token = resetToken, Password = "N3w-pa55word!", ConfirmPassword = "N3w-pa55word!" });

        await db.Entry(user).ReloadAsync();
        Assert.Null(user.EmailOtpCode);
        Assert.Null(user.LoginChallengeHash);
        Assert.NotNull((await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == browser.Id)).RevokedAt);
        Assert.Null((await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == server.Id)).RevokedAt);
    }
}
