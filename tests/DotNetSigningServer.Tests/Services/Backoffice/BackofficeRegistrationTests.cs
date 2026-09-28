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
    internal const string SdkAssembly = "P4.Backoffice.Sdk";

    internal static ServiceCollection Register(Dictionary<string, string?> settings, bool privateServer = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddP4BackofficeIntegration(configuration, new PrivateServerOptions { Enabled = privateServer });
        return services;
    }

    /// <summary>True when the inbox processor or event polling would run.</summary>
    internal static bool AnyInboxWorker(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService)
                          && (d.ImplementationType == typeof(BackofficeInboxProcessor)
                              || d.ImplementationType == typeof(BackofficePollingService)));

    internal static bool AnySdkService(IServiceCollection services) =>
        services.Any(d => d.ServiceType.Assembly.GetName().Name == SdkAssembly
                          || d.ImplementationType?.Assembly.GetName().Name == SdkAssembly);

    private static P4BackofficeProductOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<P4BackofficeProductOptions>>().Value;

    [Fact]
    public void WithoutSection_EverythingIsOffAndTheSdkIsNotRegistered()
    {
        var services = Register(new());

        var options = Resolve(services);
        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
        Assert.False(AnySdkService(services));
    }

    [Fact]
    public void AllOff_DoesNotRegisterTheSdk()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "Off",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        Assert.False(AnySdkService(services));
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
    public void UnknownMode_Fails()
    {
        var services = Register(new() { ["P4Backoffice:Mode"] = "maybe" });

        Assert.Throws<OptionsValidationException>(() => Resolve(services));
    }

    [Fact]
    public void ModuleSwitch_OverridesGlobalMode()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "Shadow",
            ["P4Backoffice:Modules:Pricing"] = "Off",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        var options = Resolve(services);
        Assert.Equal(BackofficeMode.Off, options.RequestedModeFor(BackofficeModule.Pricing));
        Assert.Equal(BackofficeMode.Shadow, options.RequestedModeFor(BackofficeModule.Docs));
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

#if P4_BACKOFFICE_SDK
    [Fact]
    public void On_WithSdk_RegistersTheClient()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        Assert.Contains(services, d => d.ServiceType == typeof(P4.Backoffice.Sdk.Client.BackofficeApiClient));
        Assert.True(BackofficeRegistration.SdkIncluded);
        Assert.True(Resolve(services).AnyEnabled);
        Assert.True(AnyInboxWorker(services));
    }
#else
    [Fact]
    public void On_WithoutSdk_StaysOff()
    {
        var services = Register(new()
        {
            ["P4Backoffice:Mode"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        var options = Resolve(services);
        Assert.Equal(BackofficeDisabledReason.SdkNotIncluded, options.DisabledReason);
        Assert.False(options.AnyEnabled);
        Assert.False(AnySdkService(services));
        Assert.False(AnyInboxWorker(services));
    }
#endif

}
