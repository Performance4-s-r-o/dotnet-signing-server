using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Backoffice;

public class BackofficeRegistrationTests
{
    internal static ServiceCollection Register(Dictionary<string, string?> settings, bool privateServer = false, string? environment = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = environment ?? Environments.Development });
        services.AddP4BackofficeIntegration(configuration, new PrivateServerOptions { Enabled = privateServer });
        return services;
    }

    /// <summary>True when the inbox processor or event polling would run.</summary>
    internal static bool AnyInboxWorker(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService)
                          && (d.ImplementationType == typeof(BackofficeInboxProcessor)
                              || d.ImplementationType == typeof(BackofficePollingService)));

    private static P4BackofficeProductOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<P4BackofficeProductOptions>>().Value;

    [Fact]
    public void WithoutSection_EverythingIsOffAndTheSdkIsNotRegistered()
    {
        var services = Register(new());

        var options = Resolve(services);
        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
    }

    [Fact]
    public void AllOff_DoesNotRegisterTheSdk()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "Off",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
            ["P4Backoffice:Environment"] = "test",
        });
        Assert.False(AnyInboxWorker(services));
    }

    [Fact]
    public void WebhookEndpointDependencies_AreAlwaysRegistered()
    {
        // The endpoint exists outside a PrivateServer and answers 404 itself while Off.
        var services = Register(new());

        Assert.Contains(services, d => d.ServiceType == typeof(BackofficeInbox));
        Assert.Contains(services, d => d.ServiceType == typeof(BackofficeInboxSignal));
        Assert.False(AnyInboxWorker(services));
    }

    [Fact]
    public void OnWithoutSecretKey_FailsWithTheEnvironmentVariableName()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(services));
        Assert.Contains("P4Backoffice__SecretKey", ex.Message);
    }

    [Fact]
    public void UnknownMode_FailsInProduction()
    {
        var services = Register(new() { ["P4Backoffice:Mode"] = "maybe" }, environment: Environments.Production);

        Assert.Throws<OptionsValidationException>(() => Resolve(services));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("Shadow")]
    public void UnknownMode_OutsideProduction_KeepsTheModulesOff(string mode)
    {
        var services = Register(new() { ["P4Backoffice:Mode"] = mode });

        var options = Resolve(services);
        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
    }

    [Fact]
    public void ModuleSwitch_OverridesGlobalMode()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:Modules:Pricing"] = "Off",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
            ["P4Backoffice:Environment"] = "test",
        });

        var options = Resolve(services);
        Assert.Equal(BackofficeMode.Off, options.RequestedModeFor(BackofficeModule.Pricing));
        Assert.Equal(BackofficeMode.On, options.RequestedModeFor(BackofficeModule.Docs));
    }

    [Fact]
    public void Options_AreValidatedOnStart()
    {
        var services = Register(new() { ["P4Backoffice:Mode"] = "On" });
        using var provider = services.BuildServiceProvider();

        // ValidateOnStart hooks into host start through IStartupValidator.
        var validator = provider.GetRequiredService<IStartupValidator>();
        Assert.Throws<OptionsValidationException>(() => validator.Validate());
    }

    [Fact]
    public void On_RegistersTheIntegration()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
            ["P4Backoffice:Environment"] = "test",
        });

        var options = Resolve(services);
        Assert.Equal(BackofficeDisabledReason.None, options.DisabledReason);
        Assert.True(options.AnyEnabled);
        Assert.True(AnyInboxWorker(services));
    }

}
