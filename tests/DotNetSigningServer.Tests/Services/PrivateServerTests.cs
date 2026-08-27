using DotNetSigningServer.Conventions;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using System.Reflection;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// A self-hosted installation runs this server as an internal API. Everything
/// built for the hosted service — billing, marketing, support — is then surface
/// with no purpose, and surface with no purpose is the kind nobody notices is
/// exposed.
/// </summary>
public class PrivateServerConventionTests
{
    private static ApplicationModel BuildModel(params string[] controllerNames)
    {
        var application = new ApplicationModel();
        foreach (var name in controllerNames)
        {
            var type = typeof(PrivateServerConventionTests).GetTypeInfo();
            var controller = new ControllerModel(type, new List<object>()) { ControllerName = name };
            application.Controllers.Add(controller);
        }
        return application;
    }

    [Fact]
    public void Removes_TheControllersThatOnlyServeTheHostedService()
    {
        var application = BuildModel("Billing", "StripeWebhook", "Home", "Seo", "Legal", "Support", "Requests");

        new PrivateServerConvention().Apply(application);

        Assert.Empty(application.Controllers);
    }

    [Fact]
    public void Keeps_WhatTheInstallationActuallyNeeds()
    {
        // The four API controllers are the product; Account and ApiTokens are how
        // an administrator obtains a key for the portal to use.
        var kept = new[]
        {
            "PdfSigningApi", "PdfTemplateApi", "PdfUtilityApi", "Api", "Account", "Admin", "ApiTokens",
        };
        var application = BuildModel(kept);

        new PrivateServerConvention().Apply(application);

        Assert.Equal(kept.Length, application.Controllers.Count);
    }

    [Fact]
    public void Removes_SigningUpButKeepsSigningIn()
    {
        var application = BuildModel("Account");
        var controller = application.Controllers[0];
        foreach (var name in new[] { "SignUp", "SignIn", "SignOut", "ChangePassword", "ResendVerification" })
        {
            controller.Actions.Add(new ActionModel(
                typeof(PrivateServerConventionTests).GetMethod(nameof(Removes_SigningUpButKeepsSigningIn))!,
                new List<object>())
            { ActionName = name });
        }

        new PrivateServerConvention().Apply(application);

        var remaining = controller.Actions.Select(a => a.ActionName).ToList();
        // Signing up is what makes this a service rather than an appliance.
        Assert.DoesNotContain("SignUp", remaining);
        Assert.DoesNotContain("ResendVerification", remaining);
        // Everything the one administrator needs stays.
        Assert.Contains("SignIn", remaining);
        Assert.Contains("ChangePassword", remaining);
    }
}

/// <summary>
/// Signing up is switched off on a self-hosted installation, so the first
/// account has to come from somewhere — and must not be openable by a string
/// somebody read in a manual.
/// </summary>
public class PrivateServerStartupCheckTests
{
    private static readonly PrivateServerOptions On = new() { Enabled = true };
    private static readonly PrivateServerOptions Off = new() { Enabled = false };

    [Fact]
    public void HostedService_ChecksNothing()
    {
        Assert.Empty(PrivateServerStartupChecks.Validate(Off, null, null, anyUserExists: false));
    }

    [Fact]
    public void NoCredentialsAndNoUsers_RefusesToStart()
    {
        // Otherwise the installation comes up and nobody can ever log in to it.
        var problems = PrivateServerStartupChecks.Validate(On, null, null, anyUserExists: false);

        Assert.Single(problems);
        Assert.Contains("nothing could create one", problems[0]);
    }

    [Fact]
    public void OnceAnAdministratorExists_CredentialsAreNoLongerNeeded()
    {
        // Leaving a password in the environment for the life of the installation
        // is worse than needing it for the first start.
        Assert.Empty(PrivateServerStartupChecks.Validate(On, null, null, anyUserExists: true));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("password")]
    [InlineData("ChangeMe")]
    [InlineData("P@ssw0rd")]
    public void WellKnownPassword_RefusesToStart(string password)
    {
        var problems = PrivateServerStartupChecks.Validate(On, "admin@example.com", password, anyUserExists: false);

        Assert.Contains(problems, p => p.Contains("well-known default"));
    }

    [Fact]
    public void ShortPassword_RefusesToStart()
    {
        var problems = PrivateServerStartupChecks.Validate(On, "admin@example.com", "short1!", anyUserExists: false);

        Assert.Contains(problems, p => p.Contains("shorter than"));
    }

    [Fact]
    public void MalformedEmail_IsReportedAlongsideAnythingElse()
    {
        // Reported together so one restart surfaces every problem.
        var problems = PrivateServerStartupChecks.Validate(On, "not-an-address", "short", anyUserExists: false);

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void GoodCredentials_Pass()
    {
        Assert.Empty(PrivateServerStartupChecks.Validate(
            On, "admin@zakaznik.cz", "correct-horse-battery-staple", anyUserExists: false));
    }
}
