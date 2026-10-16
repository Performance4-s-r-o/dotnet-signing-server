using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Moq;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #82: the sign-in and password-reset throttles key an address exactly as the account
/// lookup reads it, so spellings that reach one account (surrounding spaces, letter case)
/// share one window instead of each getting five attempts of its own. Since #80 the key also
/// carries the client IP (another IP cannot lock the owner out), and the 2FA step is keyed by
/// its sign-in challenge (see AccountSecurityTests).
/// </summary>
public class SignInRateLimitTests
{
    internal static AccountController Controller(DotNetSigningServer.Data.ApplicationDbContext? db = null, string? ip = null, AppOptions? app = null, SessionSecurityOptions? security = null, ITemplatedEmailSender? email = null)
    {
        db ??= TestHelpers.CreateInMemoryDbContext();
        var controller = new AccountController(
            db,
            new AuthService(),
            email ?? Mock.Of<ITemplatedEmailSender>(),
            TestHelpers.WrapOptions(app ?? new AppOptions()),
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()),
            new AuthAttemptLimiter(db),
            TestHelpers.WrapOptions(security ?? new SessionSecurityOptions()));
        var http = new DefaultHttpContext();
        if (ip != null) http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    /// <summary>The address as an attacker would vary it: spaces around it, other letter case.</summary>
    private static string Spelling(string address, int i) =>
        new string(' ', i % 3) + (i % 2 == 0 ? address : address.ToUpperInvariant()) + new string(' ', i / 3);

    [Fact]
    public void Key_IsTheSameForEverySpellingOfOneAddress()
    {
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal("victim@example.cz", UserEmails.Key(Spelling("victim@example.cz", i)));
        }
    }

    [Fact]
    public async Task SignIn_AttemptsFromAnotherIp_DoNotLockTheOwnerOut()
    {
        var address = $"{Guid.NewGuid():N}@example.cz";
        for (var i = 0; i < 6; i++)
        {
            await Controller(ip: "203.0.113.7").SignIn(new SignInViewModel { Email = address, Password = "wrong" });
        }

        var owner = Controller(ip: "198.51.100.20");
        await owner.SignIn(new SignInViewModel { Email = address, Password = "wrong" });
        Assert.DoesNotContain(owner.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "TooManySignInAttempts");
    }

    [Fact]
    public async Task SignIn_ManyIps_HitTheLimitPerAccount()
    {
        // A spoofed or rotating IP is a new per-IP key; the account key still counts (#80 review).
        var address = $"{Guid.NewGuid():N}@example.cz";
        var security = new SessionSecurityOptions { AccountMaxAttempts = 3 };
        for (var i = 0; i < 3; i++)
        {
            var c = Controller(ip: $"203.0.113.{i + 1}", security: security);
            await c.SignIn(new SignInViewModel { Email = address, Password = "wrong" });
            Assert.DoesNotContain(c.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "TooManySignInAttempts");
        }

        var fourth = Controller(ip: "198.51.100.99", security: security);
        await fourth.SignIn(new SignInViewModel { Email = address, Password = "wrong" });
        Assert.Contains(fourth.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "TooManySignInAttempts");
    }

    [Fact]
    public async Task SignIn_SpellingsOfOneAddress_ShareOneWindow()
    {
        var address = $"{Guid.NewGuid():N}@example.cz";

        for (var i = 0; i < 5; i++)
        {
            var controller = Controller();
            await controller.SignIn(new SignInViewModel { Email = Spelling(address, i), Password = "wrong" });
            Assert.DoesNotContain(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "TooManySignInAttempts");
        }

        var sixth = Controller();
        await sixth.SignIn(new SignInViewModel { Email = Spelling(address, 5), Password = "wrong" });
        Assert.Contains(sixth.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "TooManySignInAttempts");
    }
}
