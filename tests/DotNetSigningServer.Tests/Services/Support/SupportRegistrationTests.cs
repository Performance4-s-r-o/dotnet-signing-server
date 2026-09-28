using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Services.Support;

public class SupportRegistrationTests
{
    private static ServiceProvider Build(string? mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("reg-" + Guid.NewGuid()));
        var options = new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.None,
            BaseUrl = "https://backoffice.test",
            SecretKey = "p4sk_test_x",
            Modules = { Support = mode },
        };
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        BackofficeRegistration.AddSupport(services, options);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    public void Off_OnlyTheFormsCategories(string? mode)
    {
        using var provider = Build(mode);
        using var scope = provider.CreateScope();

        Assert.NotNull(provider.GetRequiredService<SupportCategoriesProvider>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is SupportCategoriesWorker);
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is SupportEventsHandler);
    }

    [Theory]
    [InlineData("Shadow")]
    [InlineData("On")]
    public void ShadowOrOn_RefreshesCategoriesAndHandlesEvents(string mode)
    {
        using var provider = Build(mode);
        using var scope = provider.CreateScope();

        Assert.Contains(provider.GetServices<IHostedService>(), s => s is SupportCategoriesWorker);
        Assert.Contains(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is SupportEventsHandler);
    }

    [Fact]
    public void Controller_IsActivatedWithoutTheDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddHttpClient();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("reg-" + Guid.NewGuid()));
        services.Configure<OsTicketOptions>(_ => { });
        var options = new P4BackofficeProductOptions { DisabledReason = BackofficeDisabledReason.SdkNotIncluded };
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        BackofficeRegistration.AddSupport(services, options);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // As MVC's controller activator does.
        var factory = ActivatorUtilities.CreateFactory(typeof(SupportController), Type.EmptyTypes);

        Assert.IsType<SupportController>(factory(scope.ServiceProvider, null));
    }
}
