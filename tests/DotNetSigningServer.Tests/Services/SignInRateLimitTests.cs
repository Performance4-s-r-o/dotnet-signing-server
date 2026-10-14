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
/// #82: the sign-in, 2FA and password-reset throttles key an address exactly as the account
/// lookup reads it, so spellings that reach one account (surrounding spaces, letter case)
/// share one window instead of each getting five attempts of its own.
/// </summary>
public class SignInRateLimitTests
{
    private static AccountController Controller()
    {
        var controller = new AccountController(
            TestHelpers.CreateInMemoryDbContext(),
            new AuthService(),
            Mock.Of<ITemplatedEmailSender>(),
            TestHelpers.WrapOptions(new AppOptions()),
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()));
        var http = new DefaultHttpContext();
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
    public async Task TwoFactor_SpellingsOfOneAddress_ShareOneWindow()
    {
        var address = $"{Guid.NewGuid():N}@example.cz";

        for (var i = 0; i < 5; i++)
        {
            var controller = Controller();
            await controller.TwoFactorPost(Spelling(address, i), "000000");
            Assert.Equal("VerificationCodeInvalid", controller.TempData["Error"]);
        }

        var sixth = Controller();
        await sixth.TwoFactorPost(Spelling(address, 5), "000000");
        Assert.Equal("TooManyVerificationAttempts", sixth.TempData["Error"]);
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
