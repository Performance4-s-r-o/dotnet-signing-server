using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Services.Backoffice;

/// <summary>In Production every module is On without a setting; elsewhere Off. Off is the rollback.</summary>
public class BackofficeDefaultModeTests
{
    private static readonly BackofficeModule[] Switched =
        [BackofficeModule.Docs, BackofficeModule.Consents, BackofficeModule.Pricing, BackofficeModule.Support, BackofficeModule.Billing];

    /// <summary>Options bound from settings, with the same flags the registration sets (no validation).</summary>
    private static P4BackofficeProductOptions Resolve(Dictionary<string, string?> settings, string environment, bool privateServer = false)
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(kv => "P4Backoffice:" + kv.Key, kv => kv.Value)).Build().GetSection("P4Backoffice");
        var options = section.Get<P4BackofficeProductOptions>() ?? new P4BackofficeProductOptions();
        options.DefaultOn = environment == Environments.Production;
        options.DisabledReason = BackofficeRegistration.DisabledReason(new PrivateServerOptions { Enabled = privateServer });
        return options;
    }

    [Fact]
    public void Production_WithoutSettings_EveryModuleIsOn()
    {
        var options = Resolve(new(), Environments.Production);

        Assert.All(Switched, m => Assert.Equal(BackofficeMode.On, options.ModeFor(m)));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentAndTesting_WithoutSettings_StayOff(string environment)
    {
        var options = Resolve(new(), environment);

        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
    }

    [Fact]
    public void PrivateServer_InProduction_StaysOff()
    {
        var options = Resolve(new(), Environments.Production, privateServer: true);

        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
    }

    [Fact]
    public void GlobalOff_RollsEverythingBack_BillingIncluded()
    {
        var options = Resolve(new() { ["Mode"] = "Off" }, Environments.Production);

        Assert.All(Switched, m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
    }

    [Fact]
    public void ModuleOff_RollsBackThatModuleOnly()
    {
        var options = Resolve(new() { ["Modules:Docs"] = "Off", ["Modules:Billing"] = "off" }, Environments.Production);

        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Docs));
        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Billing));
        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Pricing));
    }

    [Fact]
    public void GlobalOn_StillDoesNotSwitchBillingOn_OutsideProduction()
    {
        var options = Resolve(new() { ["Mode"] = "On" }, Environments.Development);

        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Docs));
        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Billing));
    }

    [Fact]
    public void Production_WithoutConnection_FailsLoudlyNamingTheSettings()
    {
        var options = Resolve(new(), Environments.Production);

        var problems = string.Join("\n", BackofficeOptionsValidator.Validate(options, requireBilling: true, requireEmail: true));

        Assert.Contains("BaseUrl", problems);
        Assert.Contains("SecretKey", problems);
        Assert.Contains("Environment", problems);
    }

    [Fact]
    public void Production_WithConnection_IsValidWithoutAnyModeSetting()
    {
        var options = Resolve(new()
        {
            ["BaseUrl"] = "https://backoffice.example.com",
            ["SecretKey"] = "p4sk_test_abc",
            ["Environment"] = "test",
        }, Environments.Production);

        Assert.Empty(BackofficeOptionsValidator.Validate(options, requireBilling: true, requireEmail: true));
    }
}

public class BackofficeDefaultModeRegistrationTests
{
    [Fact]
    public void Production_RegistrationValidatesTheDefaultOn_AndFailsLoudlyWithoutConnection()
    {
        var services = BackofficeRegistrationTests.Register(new(), environment: Environments.Production);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<P4BackofficeProductOptions>>().Value);
        Assert.Contains("P4Backoffice:BaseUrl", ex.Message);
        Assert.Contains("P4Backoffice:Environment", ex.Message);
    }
}
