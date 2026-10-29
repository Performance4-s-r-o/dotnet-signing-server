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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class EmailRegistrationTests
{
    private static P4BackofficeProductOptions Configured() => new()
    {
        DisabledReason = BackofficeDisabledReason.None,
        BaseUrl = "https://backoffice.test",
        SecretKey = "p4sk_test_abc",
    };

    private static ServiceProvider Build(P4BackofficeProductOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment("Development"));
        services.AddHttpClient();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("reg-" + Guid.NewGuid()));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxPayloadProtector>();
        services.AddScoped<IBackofficeOutbox, BackofficeOutbox>();
        services.AddSingleton<IOptionsMonitor<P4BackofficeProductOptions>>(new MutableOptionsMonitor<P4BackofficeProductOptions>(options));
        BackofficeRegistration.AddEmail(services, options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configured_QueuesThroughTheOutbox()
    {
        using var provider = Build(Configured());
        using var scope = provider.CreateScope();

        Assert.IsType<BackofficeOutboxEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.Empty(provider.GetRequiredService<OutboxKindFilter>().PausedKindPrefixes);
        Assert.Contains(provider.GetServices<IOutboxHandler>(), h => h is EmailRawOutboxHandler);
        Assert.Contains(provider.GetServices<IOutboxHandler>(), h => h is EmailTemplateOutboxHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is EmailEventsHandler);
    }

    [Fact]
    public void WithoutConnection_OnlyLogsAndPausesEmailItems()
    {
        using var provider = Build(new P4BackofficeProductOptions { DisabledReason = BackofficeDisabledReason.None });
        using var scope = provider.CreateScope();

        Assert.IsType<LoggingEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.Equal(["email."], provider.GetRequiredService<OutboxKindFilter>().PausedKindPrefixes);
        Assert.DoesNotContain(provider.GetServices<IOutboxHandler>(), h => h is EmailRawOutboxHandler);
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is EmailEventsHandler);
    }

    [Fact]
    public void TemplatedSender_IsAlwaysRegistered()
    {
        foreach (var options in new[] { Configured(), new P4BackofficeProductOptions() })
        {
            var services = new ServiceCollection();
            BackofficeRegistration.AddEmail(services, options);

            Assert.Contains(services, d => d.ServiceType == typeof(ITemplatedEmailSender)
                                           && d.ImplementationType == typeof(TemplatedEmailSender));
        }
    }

    [Fact]
    public void PrivateServer_IsOffEvenWithAConnection()
    {
        var options = Configured();
        options.DisabledReason = BackofficeDisabledReason.PrivateServer;
        var services = new ServiceCollection();
        BackofficeRegistration.AddEmail(services, options);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(BackofficeOutboxEmailSender));
    }

    [Fact]
    public void FromAndReplyTo_GoIntoTheQueuedPayload()
    {
        var payload = BackofficeOutboxEmailSender.BuildPayload("a@example.com", "s", "<p>x</p>", null, "P <n@example.com>", "r@example.com");

        Assert.Equal("P <n@example.com>", payload.From);
        Assert.Equal("r@example.com", payload.ReplyTo);
        Assert.Null(BackofficeOutboxEmailSender.BuildPayload("a@example.com", "s", "<p>x</p>", null, " ", null).From);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// <summary>Production must not start without the backoffice: there is no other mail provider.</summary>
public class EmailProductionStartTests
{
    private static async Task StartAsync(string environment, Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureServices(services =>
            {
                services.AddOptions<P4BackofficeProductOptions>()
                    .Bind(config.GetSection(P4BackofficeProductOptions.SectionName))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<P4BackofficeProductOptions>, BackofficeOptionsValidation>();
            })
            .Build();
        await host.StartAsync();
    }

    private static Dictionary<string, string?> Billing() => new()
    {
        ["P4Backoffice:Modules:Billing"] = "On",
        ["P4Backoffice:Modules:Pricing"] = "On",
    };

    [Fact]
    public async Task Production_WithoutBackofficeConfiguration_FailsLoudly()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync("Production", Billing()));

        Assert.Contains(ex.Failures, f => f.Contains("P4Backoffice__BaseUrl") && f.Contains("e-mail"));
    }

    [Fact]
    public async Task Production_WithUrlButNoKey_FailsLoudly()
    {
        var settings = Billing();
        settings["P4Backoffice:BaseUrl"] = "https://backoffice.example.com";
        settings["P4Backoffice:Environment"] = "live";

        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync("Production", settings));
    }

    [Fact]
    public async Task Production_WithUrlAndKey_Starts()
    {
        var settings = Billing();
        settings["P4Backoffice:BaseUrl"] = "https://backoffice.example.com";
        settings["P4Backoffice:SecretKey"] = "p4sk_live_abc";
        settings["P4Backoffice:Environment"] = "live";

        await StartAsync("Production", settings);
    }

    [Fact]
    public async Task Development_WithoutBackofficeConfiguration_Starts()
    {
        await StartAsync("Development", new Dictionary<string, string?>());
    }

    [Fact]
    public void Validator_PrivateServerIsExemptFromTheEmailRequirement()
    {
        var options = new P4BackofficeProductOptions { DisabledReason = BackofficeDisabledReason.PrivateServer };

        Assert.Empty(BackofficeOptionsValidator.Validate(options, requireEmail: true));
        Assert.NotEmpty(BackofficeOptionsValidator.Validate(new P4BackofficeProductOptions(), requireEmail: true));
    }
}
