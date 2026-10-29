using System.Net.Http.Headers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Services.Billing;
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

namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// All dependency injection for the P4 Backoffice integration; <c>Program.cs</c> only calls
/// <see cref="AddP4BackofficeIntegration"/>.
///
/// Nothing here is on a request path. The service is called through the integration's own
/// HTTP clients (written from the service's OpenAPI); there is no SDK dependency, so every
/// build — forks included — can switch the integration on.
/// </summary>
public static class BackofficeRegistration
{
    public static IServiceCollection AddP4BackofficeIntegration(
        this IServiceCollection services,
        IConfiguration configuration,
        PrivateServerOptions privateServer,
        bool defaultOn = false)
    {
        var section = configuration.GetSection(P4BackofficeProductOptions.SectionName);
        var reason = DisabledReason(privateServer);

        services.AddOptions<P4BackofficeProductOptions>()
            .Bind(section)
            .PostConfigure(options =>
            {
                options.DisabledReason = reason;
                options.DefaultOn = defaultOn;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<P4BackofficeProductOptions>, BackofficeOptionsValidation>();
        services.AddHostedService<BackofficeStartupReport>();
        AddOutboxCore(services);
        AddInboxCore(services);

        // Decided at registration time from the same section and the same rule as the
        // options above; the options pipeline itself is only available after Build().
        var snapshot = section.Get<P4BackofficeProductOptions>() ?? new P4BackofficeProductOptions();
        snapshot.DisabledReason = reason;
        snapshot.DefaultOn = defaultOn;
        if (snapshot.AnyEnabled)
        {
            AddOutboxDispatcher(services, snapshot);
            // Not through an on-prem gateway: it has no events to poll or receive.
            if (snapshot.EventsEnabled) AddInboxProcessing(services, snapshot);
        }
        AddLegalDocuments(services, snapshot);
        AddConsents(services, snapshot);
        AddEmail(services, snapshot);
        AddPricing(services, snapshot);
        AddSupport(services, snapshot);
        BillingRegistration.AddBilling(services, snapshot);
        AddErasure(services, snapshot);

        return services;
    }

    /// <summary>Kind prefix of every e-mail outbox item.</summary>
    public const string EmailKindPrefix = "email.";

    /// <summary>
    /// E-mail has one path: every message is queued into the outbox — as a service template
    /// (<c>email.template</c>) when its key is in <c>Email:TemplateKeys</c>, otherwise rendered
    /// locally (<c>email.raw</c>) — and sent by the dispatcher; <c>email.*</c> events are handled.
    /// It is on as soon as the service's address or key is configured (see
    /// <see cref="P4BackofficeProductOptions.RequestedModeFor"/>); Production refuses to start
    /// without them. Without a connection (development, a private server) the messages go to
    /// <see cref="LoggingEmailSender"/>, which delivers nothing, and queued e-mail items stay Pending.
    /// </summary>
    internal static void AddEmail(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        // Every caller goes through it; it reads TemplateKeys per message.
        services.TryAddScoped<ITemplatedEmailSender, TemplatedEmailSender>();

        if (options.ModeFor(BackofficeModule.Email) != BackofficeMode.On)
        {
            services.TryAddSingleton(new OutboxKindFilter([EmailKindPrefix]));
            services.AddScoped<IEmailSender, LoggingEmailSender>();
            return;
        }

        services.TryAddSingleton(OutboxKindFilter.None);
        services.TryAddScoped<BackofficeOutboxEmailSender>();
        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<BackofficeOutboxEmailSender>());
        services.AddSingleton<IOutboxHandler, EmailRawOutboxHandler>();
        services.AddSingleton<IOutboxHandler, EmailTemplateOutboxHandler>();
        services.AddScoped<IBackofficeEventHandler, EmailEventsHandler>();
    }

    /// <summary>
    /// Consents: always registered, because sign-up records a local <c>ConsentRecord</c> in every
    /// mode (Off only skips the outbox and the gate); the mode is read per call. The daily
    /// reconciliation with the service runs only when <c>Modules:Consents</c> is On.
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
        var secretKey = options.KeyFor(BackofficeModule.Consents);
        services.AddHttpClient(ConsentReconciliationService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = ConsentReconciliationService.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-consents");
        });
        services.AddHostedService<ConsentReconciliationService>();

        // The wording next to the checkboxes, written and versioned in the service.
        services.AddHttpClient(BackofficeConsentPromptsClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = BackofficeConsentPromptsClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-consent-prompts");
        });
        services.TryAddSingleton<BackofficeConsentPromptsClient>();
        services.TryAddSingleton(sp => new ConsentPromptsCache(
            sp.GetRequiredService<BackofficeConsentPromptsClient>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            options.ConsentPromptsTtl));
    }

    /// <summary>
    /// Legal documents by <c>Modules:Docs</c>: Off reads the hand-maintained rows (as before), On serves the
    /// service's texts with the <c>LegalDocuments</c> snapshot and Razor as fallbacks, and the
    /// cookie declaration under the cookies policy (<see cref="CookieDeclarationReader"/>).
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
            services.TryAddSingleton<ICookieDeclarationSource, NoCookieDeclarationSource>();
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.KeyFor(BackofficeModule.Docs);
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

        services.TryAddScoped<ILegalDocumentSource, BackofficeLegalDocumentSource>();
        services.TryAddScoped<DocumentsMetaUpdater>();
        services.TryAddScoped<DocumentsResync>();
        services.AddScoped<IBackofficeResync>(sp => sp.GetRequiredService<DocumentsResync>());
        services.AddScoped<IBackofficeEventHandler, DocumentEventsHandler>();
        services.AddHostedService<LegalDocumentsWarmup>();

        // Cookie declaration: the table under the cookies policy (same HttpClient and key).
        services.TryAddSingleton(sp => new CookieDeclarationReader(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CookieDeclarationReader>>(),
            options.DocumentsTtl));
        services.TryAddSingleton<ICookieDeclarationSource>(sp => sp.GetRequiredService<CookieDeclarationReader>());
        services.AddScoped<IBackofficeEventHandler, CookieDeclarationEventsHandler>();
        services.AddScoped<IBackofficeResync, CookieDeclarationResync>();
        services.AddHostedService<CookieDeclarationWarmup>();
    }

    /// <summary>
    /// Credit prices by <c>Modules:Pricing</c>: Off serves <c>Billing:*</c> as before; On serves the snapshot (configured prices as the
    /// fallback) and the billing service charges the Stripe Price of the pack's lookup key.
    /// Price-change notices: <see cref="PriceChangeMonitorService"/> while Off; On replaces it by
    /// <see cref="PriceScheduledHandler"/> / <see cref="PriceEffectiveHandler"/> and the daily
    /// <see cref="PricingUpcomingCheck"/>. <c>price.sync_failed</c> is logged as an error.
    /// <see cref="ICreditPricingProvider"/> is always registered.
    /// </summary>
    internal static void AddPricing(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ConfigCreditPricingProvider(sp.GetRequiredService<IOptionsMonitor<BillingOptions>>()));

        var mode = options.ModeFor(BackofficeModule.Pricing);
        // Price-change notices: the local monitor while Pricing is Off, the price.* events when On.
        if (mode == BackofficeMode.Off)
        {
            services.AddHostedService<PriceChangeMonitorService>();
            services.TryAddSingleton<ICreditPricingProvider>(sp => sp.GetRequiredService<ConfigCreditPricingProvider>());
            return;
        }

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.KeyFor(BackofficeModule.Pricing);
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
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PricingSnapshotRefresher>>()));
        services.AddHostedService<PricingSnapshotWorker>();
        services.TryAddScoped<PricingResync>();
        services.AddScoped<IBackofficeResync>(sp => sp.GetRequiredService<PricingResync>());
        AddPriceEventHandler<PriceScheduledHandler>(services);
        AddPriceEventHandler<PriceEffectiveHandler>(services);
        AddPriceEventHandler<PriceUnscheduledHandler>(services);
        services.AddScoped<IBackofficeEventHandler, PriceSyncFailedHandler>();
        services.AddHostedService<PricingUpcomingCheck>();

        services.TryAddSingleton<BackofficeCreditPricingProvider>();
        services.TryAddSingleton<ICreditPricingProvider>(sp => sp.GetRequiredService<BackofficeCreditPricingProvider>());
    }

    /// <summary>
    /// Support form by <c>Modules:Support</c>: Off sends tickets to osTicket directly
    /// (as before). On queues every ticket into the outbox (<c>support.ticket</c>),
    /// attempts it right away with a short limit, and offers the service's categories.
    /// <c>support.ticket_failed</c> is logged as an error while On. The ticket handler is
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
        var secretKey = options.KeyFor(BackofficeModule.Support);
        services.AddHttpClient(SupportCategoriesClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = SupportCategoriesClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-support");
        });
        services.TryAddSingleton<SupportCategoriesClient>();
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<SupportCategoriesWorker>(sp));
        services.AddHostedService(sp => sp.GetRequiredService<SupportCategoriesWorker>());
        services.AddScoped<IBackofficeEventHandler, SupportEventsHandler>();
    }

    /// <summary>
    /// Account erasure (#217): the service behind the account page is always there (the local
    /// erasure does not need the backoffice). While any module is On it also answers
    /// <c>subject.erasure.requested</c>; the outbox kinds <c>subject.erase</c> and
    /// <c>subject.erasure.ack</c> are sent by the dispatcher (<see cref="AddOutboxDispatcher"/>).
    /// </summary>
    internal static void AddErasure(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<DotNetSigningServer.Services.Erasure.AccountErasureService>();
        // The erasure queue is a service feature (not a gateway's, not e-mail's).
        if (!options.AnyEnabledBeyondEmail) return;
        services.AddScoped<IBackofficeEventHandler, ErasureRequestedHandler>();
    }

    /// <summary>A <c>price.*</c> handler, also resolvable as itself.</summary>
    private static void AddPriceEventHandler<T>(IServiceCollection services)
        where T : class, IBackofficeEventHandler
    {
        services.TryAddScoped<T>();
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
        services.TryAddScoped<IBackofficeOutbox, BackofficeOutbox>();
    }

    /// <summary>
    /// Sending: only when a module is On (and so never on a PrivateServer, except for e-mail through its gateway, or in a
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
        services.AddSingleton<IOutboxHandler, ErasureRequestOutboxHandler>();
        services.AddSingleton<IOutboxHandler, ErasureAckOutboxHandler>();
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
    /// Processing and polling: only when a module is On. Modules add their
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

    /// <summary>What forces every module Off: a private (self-hosted) server.</summary>
    public static BackofficeDisabledReason DisabledReason(PrivateServerOptions privateServer) =>
        privateServer.Enabled ? BackofficeDisabledReason.PrivateServer : BackofficeDisabledReason.None;
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

        if (o.DisabledReason == BackofficeDisabledReason.None && o.OnPremGatewayRequested)
        {
            logger.LogWarning("OnPremGateway is set but this is not a private server; ignored");
        }

        switch (o.DisabledReason)
        {
            case BackofficeDisabledReason.PrivateServer:
                if (o.GatewayOnly)
                {
                    logger.LogInformation("PrivateServer: only e-mail, through the notify-gateway; the rest of the backoffice integration is disabled");
                    break;
                }
                logger.LogInformation("PrivateServer: backoffice integration disabled");
                if (!string.IsNullOrWhiteSpace(o.SecretKey))
                {
                    logger.LogWarning(
                        "PrivateServer: {Setting} is set but never used; remove it from this installation",
                        BackofficeOptionsValidator.Setting("SecretKey"));
                }
                break;
        }

        foreach (var (setting, value) in BackofficeOptionsValidator.InvalidModes(o))
        {
            logger.LogWarning("Backoffice: {Setting} is \"{Value}\"; expected Off or On. The module stays Off", setting, value);
        }

        var emailMode = o.ModeFor(BackofficeModule.Email);
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
                "Backoffice: Docs={Docs} Consents={Consents} Email={Email} Pricing={Pricing} Support={Support} Billing={Billing}",
                o.ModeFor(BackofficeModule.Docs), o.ModeFor(BackofficeModule.Consents),
                o.ModeFor(BackofficeModule.Email), o.ModeFor(BackofficeModule.Pricing),
                o.ModeFor(BackofficeModule.Support), o.ModeFor(BackofficeModule.Billing));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
