using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class EmailRegistrationTests
{
    private static ServiceProvider Build(string? emailMode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("reg-" + Guid.NewGuid()));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxPayloadProtector>();
        services.AddSingleton<OutboxCircuitBreaker>();
        services.AddScoped<IBackofficeOutbox, BackofficeOutbox>();
        services.Configure<ResendOptions>(_ => { });
        BackofficeRegistration.AddEmail(services, new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.None,
            Modules = { Email = emailMode },
        });
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public void NotOn_SendsThroughResendAndPausesEmailItems(string? mode)
    {
        using var provider = Build(mode);
        using var scope = provider.CreateScope();

        Assert.IsType<ResendEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.Equal(["email."], provider.GetRequiredService<OutboxKindFilter>().PausedKindPrefixes);
        Assert.DoesNotContain(provider.GetServices<IOutboxHandler>(), h => h is EmailRawOutboxHandler);
        Assert.Empty(provider.GetServices<IOutboxFallback>());
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is EmailEventsHandler);
    }

    [Fact]
    public void On_QueuesThroughTheOutboxWithBreakGlass()
    {
        using var provider = Build("On");
        using var scope = provider.CreateScope();

        Assert.IsType<BackofficeOutboxEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ResendEmailSender>());
        Assert.Empty(provider.GetRequiredService<OutboxKindFilter>().PausedKindPrefixes);
        Assert.Contains(provider.GetServices<IOutboxHandler>(), h => h is EmailRawOutboxHandler);
        Assert.IsType<BreakGlassEmailFallback>(Assert.Single(provider.GetServices<IOutboxFallback>()));
        Assert.Contains(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is EmailEventsHandler);
    }

    [Fact]
    public void DisabledIntegration_IsOffEvenWhenEmailSaysOn()
    {
        var services = new ServiceCollection();
        BackofficeRegistration.AddEmail(services, new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.PrivateServer,
            Modules = { Email = "On" },
        });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(BackofficeOutboxEmailSender));
    }
}
