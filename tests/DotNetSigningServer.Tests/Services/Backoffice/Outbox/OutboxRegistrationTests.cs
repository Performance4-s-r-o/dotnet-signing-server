using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class OutboxRegistrationTests
{
    private static readonly Dictionary<string, string?> On = new()
    {
        ["P4Backoffice:Mode"] = "On",
        ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
        ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
    };

    private static bool HasDispatcher(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(BackofficeOutboxDispatcher));

    [Fact]
    public void AllOff_RegistersTheOutboxButNoDispatcher()
    {
        var services = BackofficeRegistrationTests.Register(new());

        Assert.Contains(services, d => d.ServiceType == typeof(IBackofficeOutbox));
        Assert.Contains(services, d => d.ServiceType == typeof(OutboxSignal));
        Assert.False(HasDispatcher(services));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(OutboxProcessor));
    }

    [Fact]
    public void PrivateServer_NeverRegistersTheDispatcher()
    {
        var services = BackofficeRegistrationTests.Register(new(On), privateServer: true);

        Assert.False(HasDispatcher(services));
    }

    [Fact]
    public void ModuleOn_RegistersTheDispatcherWhenTheBuildHasTheSdk()
    {
        var services = BackofficeRegistrationTests.Register(new(On));

        // A build without the SDK keeps every module Off, and so sends nothing.
        Assert.Equal(BackofficeRegistration.SdkIncluded, HasDispatcher(services));
    }

    [Fact]
    public void Shadow_OnOneModule_IsEnoughForTheDispatcher()
    {
        var services = BackofficeRegistrationTests.Register(new()
        {
            ["P4Backoffice:Modules:Consents"] = "Shadow",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        Assert.Equal(BackofficeRegistration.SdkIncluded, HasDispatcher(services));
    }
}
