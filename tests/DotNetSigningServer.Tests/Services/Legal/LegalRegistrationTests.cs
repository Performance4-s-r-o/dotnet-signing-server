using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Services.Legal;

public class LegalRegistrationTests
{
    private static bool Has<T>(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(T));

    private static bool HasWarmup(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(LegalDocumentsWarmup));

    [Fact]
    public void Off_ReadsOnlyTheManualRows_AndTalksToNothing()
    {
        using var host = new LegalDocsTestHost("Off");
        using var scope = host.Services.CreateScope();

        var source = Assert.IsType<DbLegalDocumentSource>(scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>());
        Assert.False(source.IncludeSnapshots);
        Assert.Null(host.Services.GetService<BackofficeDocumentsClient>());
    }

    [Fact]
    public void Shadow_RendersFromTheDatabaseWithoutSnapshots()
    {
        using var host = new LegalDocsTestHost("Shadow");
        using var scope = host.Services.CreateScope();

        Assert.IsType<ShadowLegalDocumentSource>(scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>());
        Assert.False(scope.ServiceProvider.GetRequiredService<DbLegalDocumentSource>().IncludeSnapshots);
        Assert.Empty(scope.ServiceProvider.GetServices<IBackofficeEventHandler>());
        Assert.Empty(scope.ServiceProvider.GetServices<IBackofficeResync>());
    }

    [Fact]
    public void On_UsesTheServiceWithTheSnapshotAsFallback()
    {
        using var host = new LegalDocsTestHost("On");
        using var scope = host.Services.CreateScope();

        Assert.IsType<BackofficeLegalDocumentSource>(scope.ServiceProvider.GetRequiredService<ILegalDocumentSource>());
        Assert.True(scope.ServiceProvider.GetRequiredService<DbLegalDocumentSource>().IncludeSnapshots);
        Assert.IsType<DocumentEventsHandler>(Assert.Single(scope.ServiceProvider.GetServices<IBackofficeEventHandler>()));
        Assert.IsType<DocumentsResync>(Assert.Single(scope.ServiceProvider.GetServices<IBackofficeResync>()));
    }

    [Fact]
    public void Integration_WithoutSection_KeepsTodaysBehaviour()
    {
        var services = BackofficeRegistrationTests.Register(new());

        using var provider = services.BuildServiceProvider();
        Assert.Contains(services, d => d.ServiceType == typeof(ILegalDocumentSource));
        Assert.False(Has<BackofficeDocumentsClient>(services));
        Assert.False(HasWarmup(services));
    }

    [Fact]
    public void Integration_DocsOn_RegistersTheServiceSourceOnlyWhenTheBuildHasTheSdk()
    {
        var services = BackofficeRegistrationTests.Register(new()
        {
            ["P4Backoffice:Modules:Docs"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        });

        Assert.Equal(BackofficeRegistration.SdkIncluded, HasWarmup(services));
        Assert.Equal(BackofficeRegistration.SdkIncluded, Has<BackofficeDocumentsClient>(services));
    }

    [Fact]
    public void Integration_PrivateServer_NeverTalksToTheService()
    {
        var services = BackofficeRegistrationTests.Register(new()
        {
            ["P4Backoffice:Modules:Docs"] = "On",
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        }, privateServer: true);

        Assert.False(HasWarmup(services));
        Assert.False(Has<BackofficeDocumentsClient>(services));
    }
}
