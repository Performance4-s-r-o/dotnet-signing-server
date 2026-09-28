using System.Net.Http.Headers;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
#if P4_BACKOFFICE_SDK
using DotNetSigningServer.Services.Backoffice.Sdk;
#endif

namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// All dependency injection for the P4 Backoffice integration; <c>Program.cs</c> only calls
/// <see cref="AddP4BackofficeIntegration"/>.
///
/// Nothing here is on a request path: the SDK is registered only when a module is Shadow or
/// On, and registering it opens no connection.
/// </summary>
public static class BackofficeRegistration
{
    /// <summary>Whether this build contains the SDK (<c>UseP4BackofficeSdk=true</c>).</summary>
    public const bool SdkIncluded =
#if P4_BACKOFFICE_SDK
        true;
#else
        false;
#endif

    public static IServiceCollection AddP4BackofficeIntegration(
        this IServiceCollection services,
        IConfiguration configuration,
        PrivateServerOptions privateServer)
    {
        var section = configuration.GetSection(P4BackofficeProductOptions.SectionName);
        var reason = DisabledReason(privateServer, SdkIncluded);

        services.AddOptions<P4BackofficeProductOptions>()
            .Bind(section)
            .PostConfigure(options => options.DisabledReason = reason)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<P4BackofficeProductOptions>, BackofficeOptionsValidation>();
        services.AddHostedService<BackofficeStartupReport>();
        AddOutboxCore(services);
        AddInboxCore(services);

        // Decided at registration time from the same section and the same rule as the
        // options above; the options pipeline itself is only available after Build().
        var snapshot = section.Get<P4BackofficeProductOptions>() ?? new P4BackofficeProductOptions();
        snapshot.DisabledReason = reason;
        if (snapshot.AnyEnabled)
        {
#if P4_BACKOFFICE_SDK
            // The SDK's "P4Backoffice" HttpClient keeps its own 3 s timeout; nothing is added to it.
            services.AddP4BackofficeSdk(section);
#endif
            AddOutboxDispatcher(services, snapshot);
            AddInboxProcessing(services, snapshot);
        }

        return services;
    }

    /// <summary>
    /// Always registered: enqueueing only adds rows to the caller's DbContext, and the admin
    /// overview reads them. Nothing here talks to the service.
    /// </summary>
    private static void AddOutboxCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxSignal>();
        services.TryAddSingleton<OutboxPayloadProtector>();
        services.TryAddSingleton<OutboxCircuitBreaker>();
        services.TryAddScoped<IBackofficeOutbox, BackofficeOutbox>();
    }

    /// <summary>
    /// Sending: only when a module is Shadow or On (and so never on a PrivateServer or in a
    /// build without the SDK). Its own client, separate from the SDK's 3 s one.
    /// </summary>
    private static void AddOutboxDispatcher(IServiceCollection services, P4BackofficeProductOptions options)
    {
        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(OutboxProcessor.HttpClientName, client =>
        {
            // Validated on start (absolute https URL); paths are relative to the root.
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = OutboxProcessor.AttemptTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-outbox");
        });
        services.TryAddSingleton<OutboxProcessor>();
        services.AddHostedService<BackofficeOutboxDispatcher>();
    }

    /// <summary>
    /// Always registered, because the webhook endpoint always exists (outside a
    /// PrivateServer): it answers 404 itself while the integration is Off.
    /// </summary>
    private static void AddInboxCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackofficeInboxSignal>();
        services.TryAddScoped<BackofficeInbox>();
    }

    /// <summary>
    /// Processing and polling: only when a module is Shadow or On. Modules add their
    /// <see cref="IBackofficeEventHandler"/> and <see cref="IBackofficeResync"/> as scoped services.
    /// </summary>
    private static void AddInboxProcessing(IServiceCollection services, P4BackofficeProductOptions options)
    {
        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(BackofficePollingService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = BackofficePollingService.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-events");
        });
        services.TryAddSingleton<LoggingBackofficeEventHandler>();
        services.TryAddScoped<EventHandlerRegistry>();
        services.AddHostedService<BackofficeInboxProcessor>();
        services.AddHostedService<BackofficePollingService>();
    }

    /// <summary>What forces every module Off: PrivateServer wins over a missing SDK.</summary>
    public static BackofficeDisabledReason DisabledReason(PrivateServerOptions privateServer, bool sdkIncluded) =>
        privateServer.Enabled ? BackofficeDisabledReason.PrivateServer
        : !sdkIncluded ? BackofficeDisabledReason.SdkNotIncluded
        : BackofficeDisabledReason.None;
}

/// <summary>
/// Logs the effective integration state once at startup, including the configurations that
/// are allowed but probably not intended.
/// </summary>
internal sealed class BackofficeStartupReport(
    IOptions<P4BackofficeProductOptions> options,
    IHostEnvironment environment,
    ILogger<BackofficeStartupReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;

        switch (o.DisabledReason)
        {
            case BackofficeDisabledReason.PrivateServer:
                logger.LogInformation("PrivateServer: backoffice integration disabled");
                if (!string.IsNullOrWhiteSpace(o.SecretKey))
                {
                    logger.LogWarning(
                        "PrivateServer: {Setting} is set but never used; remove it from this installation",
                        BackofficeOptionsValidator.Setting("SecretKey"));
                }
                break;
            case BackofficeDisabledReason.SdkNotIncluded when o.AnyRequested:
                logger.LogWarning(
                    "Backoffice integration is configured but this build does not include the SDK "
                    + "(UseP4BackofficeSdk=false); every module stays Off");
                break;
        }

        if (BackofficeOptionsValidator.IsTestKeyInProduction(o, environment.IsProduction()))
        {
            logger.LogWarning("Backoffice: production is using a test key ({KeyPrefix})",
                BackofficeOptionsValidator.TestKeyPrefix + "…");
        }

        if (o.AnyEnabled)
        {
            logger.LogInformation(
                "Backoffice: Docs={Docs} Consents={Consents} Email={Email} Pricing={Pricing} Support={Support}",
                o.ModeFor(BackofficeModule.Docs), o.ModeFor(BackofficeModule.Consents),
                o.ModeFor(BackofficeModule.Email), o.ModeFor(BackofficeModule.Pricing),
                o.ModeFor(BackofficeModule.Support));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
