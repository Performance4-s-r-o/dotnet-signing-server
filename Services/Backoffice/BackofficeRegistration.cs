using System.Net.Http.Headers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Services.Support;
using Microsoft.Extensions.Caching.Memory;
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
        AddLegalDocuments(services, snapshot);
        AddConsents(services, snapshot);
        AddEmail(services, snapshot);
        AddPricing(services, snapshot);
        AddSupport(services, snapshot);

        return services;
    }

    /// <summary>Kind prefix of every e-mail outbox item.</summary>
    public const string EmailKindPrefix = "email.";

    /// <summary>
    /// E-mail by <c>Modules:Email</c>: On queues every message into the outbox — as a service
    /// template (<c>email.template</c>) when its key is in <c>Email:TemplateKeys</c>, otherwise
    /// rendered locally (<c>email.raw</c>) — with the break-glass fallback and handles
    /// <c>email.*</c> events; Off and Shadow send through Resend directly, as before (Shadow only
    /// compares the listed templates with the service's rendering in the background: the
    /// service has no "log only" send, so it would deliver every message twice). <see cref="ResendEmailSender"/> is always registered as its own type too.
    /// While not On, queued e-mail items stay Pending and are sent once the module is On again.
    /// </summary>
    internal static void AddEmail(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ResendEmailSender>();
        // Every caller goes through it; it reads TemplateKeys per message.
        services.TryAddScoped<ITemplatedEmailSender, TemplatedEmailSender>();

        var mode = options.ModeFor(BackofficeModule.Email);
        if (mode != BackofficeMode.On)
        {
            services.TryAddSingleton(new OutboxKindFilter([EmailKindPrefix]));
            services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<ResendEmailSender>());
            if (mode == BackofficeMode.Shadow) AddTemplateShadow(services, options);
            return;
        }

        services.TryAddSingleton(OutboxKindFilter.None);
        services.TryAddScoped<BackofficeOutboxEmailSender>();
        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<BackofficeOutboxEmailSender>());
        services.AddSingleton<IOutboxHandler, EmailRawOutboxHandler>();
        services.AddSingleton<IOutboxHandler, EmailTemplateOutboxHandler>();
        services.TryAddSingleton<BreakGlassEmailFallback>();
        services.AddSingleton<IOutboxFallback>(sp => sp.GetRequiredService<BreakGlassEmailFallback>());
        services.AddScoped<IBackofficeEventHandler, EmailEventsHandler>();
    }

    /// <summary>
    /// Email=Shadow: templates listed in <c>TemplateKeys</c> are sent locally and compared with
    /// the service's rendering in the background (<see cref="TemplateShadowComparer"/>).
    /// </summary>
    private static void AddTemplateShadow(IServiceCollection services, P4BackofficeProductOptions options)
    {
        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(TemplateShadowComparer.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TemplateShadowComparer.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-templates");
        });
        services.TryAddSingleton<TemplateShadowComparer>();
        services.AddHostedService(sp => sp.GetRequiredService<TemplateShadowComparer>());
    }

    /// <summary>
    /// Consents: always registered, because sign-up records a local <c>ConsentRecord</c> in every
    /// mode (Off only skips the outbox and the gate); the mode is read per call. The daily
    /// reconciliation with the service runs only when <c>Modules:Consents</c> is Shadow or On.
    /// The gate itself (<c>RequireCurrentConsentFilter</c>) is added to MVC by <c>Program.cs</c>,
    /// outside a PrivateServer.
    /// </summary>
    internal static void AddConsents(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.TryAddSingleton<ConsentStatusCache>();
        services.AddSingleton<IDocumentChangeListener>(sp => sp.GetRequiredService<ConsentStatusCache>());
        services.TryAddSingleton<ConsentNoticeProvider>();
        services.TryAddScoped<ConsentDocumentResolver>();
        services.TryAddScoped<ConsentService>();
        services.TryAddScoped<ConsentBackfill>();
        services.TryAddScoped<IConsentStatusProvider, ConsentStatusProvider>();

        if (options.ModeFor(BackofficeModule.Consents) == BackofficeMode.Off)
        {
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(ConsentReconciliationService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = ConsentReconciliationService.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-consents");
        });
        services.AddHostedService<ConsentReconciliationService>();
    }

    /// <summary>
    /// Legal documents by <c>Modules:Docs</c>: Off reads the hand-maintained rows (as before),
    /// Shadow does the same and compares with the service in the background, On serves the
    /// service's texts with the <c>LegalDocuments</c> snapshot and Razor as fallbacks.
    /// </summary>
    internal static void AddLegalDocuments(IServiceCollection services, P4BackofficeProductOptions options)
    {
        var mode = options.ModeFor(BackofficeModule.Docs);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<LegalDocumentsSnapshotWriter>();
        // On: the hand-maintained rows are the fallback, and the snapshot is read with them.
        services.TryAddScoped(sp => new DbLegalDocumentSource(
            sp.GetRequiredService<ApplicationDbContext>(),
            sp.GetRequiredService<ILogger<DbLegalDocumentSource>>(),
            includeSnapshots: mode == BackofficeMode.On,
            sp.GetRequiredService<TimeProvider>()));

        if (mode == BackofficeMode.Off)
        {
            services.TryAddScoped<ILegalDocumentSource>(sp => sp.GetRequiredService<DbLegalDocumentSource>());
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(BackofficeDocumentsClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = BackofficeDocumentsClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-documents");
        });
        services.AddMemoryCache();
        services.TryAddSingleton<BackofficeDocumentsClient>();
        services.TryAddSingleton(sp => new BackofficeDocumentsCache(
            sp.GetRequiredService<BackofficeDocumentsClient>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            options.DocumentsTtl));
        services.TryAddSingleton<LegalDocumentRefresher>();

        if (mode == BackofficeMode.Shadow)
        {
            services.TryAddScoped<ILegalDocumentSource, ShadowLegalDocumentSource>();
            return;
        }

        services.TryAddScoped<ILegalDocumentSource, BackofficeLegalDocumentSource>();
        services.TryAddScoped<DocumentsMetaUpdater>();
        services.TryAddScoped<DocumentsResync>();
        services.AddScoped<IBackofficeResync>(sp => sp.GetRequiredService<DocumentsResync>());
        services.AddScoped<IBackofficeEventHandler, DocumentEventsHandler>();
        services.AddHostedService<LegalDocumentsWarmup>();
    }

    /// <summary>
    /// Credit prices by <c>Modules:Pricing</c>: Off serves <c>Billing:*</c> as before; Shadow does
    /// the same, keeps the price-list snapshot up to date in the background and logs how it
    /// differs from the configuration; On serves the snapshot (configured prices as the
    /// fallback) and Checkout charges the Stripe Price of the pack's lookup key.
    /// Price-change notices: <see cref="PriceChangeMonitorService"/> while Off or Shadow (Shadow
    /// handlers of <c>price.*</c> only log what they would do); On replaces it by
    /// <see cref="PriceScheduledHandler"/> / <see cref="PriceEffectiveHandler"/> and the daily
    /// <see cref="PricingUpcomingCheck"/>. <c>price.sync_failed</c> is logged as an error.
    /// <see cref="ICreditPricingProvider"/> and <see cref="StripePriceResolver"/> are always
    /// registered; the resolver only acts on packs with a lookup key, which Off never has.
    /// </summary>
    internal static void AddPricing(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ConfigCreditPricingProvider(sp.GetRequiredService<IOptionsMonitor<BillingOptions>>()));
        services.TryAddSingleton(sp =>
        {
            var apiKey = sp.GetService<IOptions<StripeOptions>>()?.Value.ApiKey;
            return new StripePriceResolver(
                string.IsNullOrWhiteSpace(apiKey) ? null : new Stripe.StripeClient(apiKey),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<StripePriceResolver>>());
        });

        var mode = options.ModeFor(BackofficeModule.Pricing);
        // Price-change notices: the local monitor unless Pricing is On, then the price.* events.
        // Never both (Shadow handlers only log).
        if (mode != BackofficeMode.On)
        {
            services.AddHostedService<PriceChangeMonitorService>();
        }
        if (mode == BackofficeMode.Off)
        {
            services.TryAddSingleton<ICreditPricingProvider>(sp => sp.GetRequiredService<ConfigCreditPricingProvider>());
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(BackofficePricingClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = BackofficePricingClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-pricing");
        });
        services.TryAddSingleton<BackofficePricingClient>();
        services.TryAddSingleton<PricingSnapshotHolder>();
        services.TryAddSingleton(sp => new PricingSnapshotRefresher(
            sp.GetRequiredService<BackofficePricingClient>(),
            sp.GetRequiredService<PricingSnapshotHolder>(),
            sp.GetRequiredService<StripePriceResolver>(),
            sp.GetRequiredService<ConfigCreditPricingProvider>(),
            sp.GetRequiredService<IOptionsMonitor<StripeOptions>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PricingSnapshotRefresher>>(),
            mode));
        services.AddHostedService<PricingSnapshotWorker>();
        services.TryAddScoped<PricingResync>();
        services.AddScoped<IBackofficeResync>(sp => sp.GetRequiredService<PricingResync>());
        AddPriceEventHandler<PriceScheduledHandler>(services, mode);
        AddPriceEventHandler<PriceEffectiveHandler>(services, mode);
        AddPriceEventHandler<PriceUnscheduledHandler>(services, mode);
        services.AddScoped<IBackofficeEventHandler, PriceSyncFailedHandler>();
        services.AddHostedService<PricingUpcomingCheck>();

        if (mode == BackofficeMode.Shadow)
        {
            services.TryAddSingleton<ICreditPricingProvider>(sp => sp.GetRequiredService<ConfigCreditPricingProvider>());
            return;
        }

        services.TryAddSingleton<BackofficeCreditPricingProvider>();
        services.TryAddSingleton<ICreditPricingProvider>(sp => sp.GetRequiredService<BackofficeCreditPricingProvider>());
    }

    /// <summary>
    /// Support form by <c>Modules:Support</c>: Off and Shadow send tickets to osTicket directly
    /// (as before; Shadow only keeps the service's categories up to date and logs how they
    /// differ from the form's). On queues every ticket into the outbox (<c>support.ticket</c>),
    /// attempts it right away with a short limit, and offers the service's categories.
    /// <c>support.ticket_failed</c> is logged as an error while Shadow or On. The ticket handler is
    /// registered with the dispatcher (<see cref="AddOutboxDispatcher"/>), so tickets queued while
    /// On are still delivered after a switch back to Off.
    /// </summary>
    internal static void AddSupport(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SupportCategoriesHolder>();
        services.TryAddSingleton<SupportCategoriesProvider>();

        var mode = options.ModeFor(BackofficeModule.Support);
        if (mode == BackofficeMode.Off)
        {
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(SupportCategoriesClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = SupportCategoriesClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-support");
        });
        services.TryAddSingleton<SupportCategoriesClient>();
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<SupportCategoriesWorker>(sp, mode));
        services.AddHostedService(sp => sp.GetRequiredService<SupportCategoriesWorker>());
        services.AddScoped<IBackofficeEventHandler, SupportEventsHandler>();
    }

    /// <summary>A <c>price.*</c> handler that acts by the Pricing mode, also resolvable as itself.</summary>
    private static void AddPriceEventHandler<T>(IServiceCollection services, BackofficeMode mode)
        where T : class, IBackofficeEventHandler
    {
        services.TryAddScoped(sp => ActivatorUtilities.CreateInstance<T>(sp, mode));
        services.AddScoped<IBackofficeEventHandler>(sp => sp.GetRequiredService<T>());
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
        // Handlers of every kind a module may have queued: items stay valid when their module
        // is switched Off again later.
        services.AddSingleton<IOutboxHandler, ConsentOutboxHandler>();
        services.AddSingleton<IOutboxHandler, SupportTicketOutboxHandler>();
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

        var emailMode = o.ModeFor(BackofficeModule.Email);
        if (emailMode == BackofficeMode.Shadow)
        {
            logger.LogWarning(
                "Backoffice: Modules:Email=Shadow sends e-mail through Resend as with Off (the service would send every "
                + "message twice); templates in Email:TemplateKeys are only compared with the service's rendering");
        }
        if (o.ModeFor(BackofficeModule.Support) == BackofficeMode.Shadow)
        {
            logger.LogInformation(
                "Backoffice: Modules:Support=Shadow sends tickets to osTicket as with Off; the service's categories are only compared");
        }
        var unknownKeys = o.Email.TemplateKeys
            .Where(k => !EmailTemplateVariables.Names.ContainsKey(k?.Trim() ?? ""))
            .ToList();
        if (unknownKeys.Count > 0)
        {
            logger.LogWarning("Backoffice: Email:TemplateKeys contains unknown template keys ({Keys}); they are ignored",
                string.Join(", ", unknownKeys));
        }
        if (emailMode != BackofficeMode.Off && o.Email.TemplateKeys.Count > 0)
        {
            logger.LogInformation("Backoffice: e-mail templates from the service: {Keys}",
                string.Join(", ", o.Email.TemplateKeys));
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
