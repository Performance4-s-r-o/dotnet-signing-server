using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
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
    }
#endif

}
