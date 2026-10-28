using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication;
#if DEBUG
using Testcontainers.PostgreSql;
#endif
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;

// A shared Coolify variable that does not exist arrives as its placeholder text
// ({{environment.X}}); treat it as unset before anything binds configuration.
var unresolvedPlaceholders = DotNetSigningServer.Options.EnvValues.ScrubUnresolvedPlaceholders();
if (unresolvedPlaceholders.Count > 0)
{
    Console.WriteLine($"[startup] Unresolved Coolify placeholders treated as unset: {string.Join(' ', unresolvedPlaceholders)}");
}

var builder = WebApplication.CreateBuilder(args);

// Release step (#85): migrate and exit, serve nothing.
var migrateOnly = args.Contains(DotNetSigningServer.Services.DatabaseMigrator.MigrateArgument);

// Error monitoring. Reads the "Sentry" config section (DSN, Debug,
// TracesSampleRate). Captures unhandled exceptions plus everything logged at
// LogError/LogCritical — including LokiExceptionMiddleware's handled-exception
// path — via the Sentry ILogger integration.
//
// Only initialise Sentry when a DSN is configured (docker/prod set Sentry__Dsn).
// Local `dotnet watch` runs without one, and the SDK throws on a null DSN, so we
// skip it here instead of crashing at startup.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Sentry:Dsn"]))
{
    builder.WebHost.UseSentry();
}

// Persist data protection keys so antiforgery/cookies survive restarts and container re-creations.
var dataProtectionPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH")
                        ?? Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
Directory.CreateDirectory(dataProtectionPath);
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

// The data protection keys don't just cover cookies/antiforgery — PdfSigningApiController
// encrypts per-request TSA usernames/passwords with them. Unprotected, the key ring is
// plaintext XML in the volume, so volume access + DB access decrypts every stored
// TSA credential. Encrypt the ring with a deployment-provided certificate when one is set.
var keyProtectionCertBase64 = builder.Configuration["DataProtection:CertificateBase64"];
var keyProtectionCertPassword = builder.Configuration["DataProtection:CertificatePassword"];
if (!string.IsNullOrWhiteSpace(keyProtectionCertBase64))
{
    var certBytes = Convert.FromBase64String(
        new string(keyProtectionCertBase64.Where(c => !char.IsWhiteSpace(c)).ToArray()));
    var keyProtectionCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
        certBytes, keyProtectionCertPassword);
    dataProtection.ProtectKeysWithCertificate(keyProtectionCert);
}
else if (DotNetSigningServer.Options.DataProtectionKeyPolicy.Evaluate(
             builder.Environment.IsProduction(), migrateOnly, builder.Configuration["DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS"])
         is { } plaintextOutcome)
{
    if (plaintextOutcome.Refuse)
    {
        throw new InvalidOperationException(plaintextOutcome.Message(dataProtectionPath));
    }
    Console.WriteLine("[startup] WARNING: " + plaintextOutcome.Message(dataProtectionPath));
}
// #106: who may name the client. CF-Connecting-IP only from cloudflared (TRUSTED_CF_PROXIES),
// X-Forwarded-For only from the reverse proxy (TRUSTED_PROXIES, or an explicit ForwardedHeaders
// list; `none` = from nobody; unset = the private ranges, as before).
var clientIpTrust = ClientIpTrust.From(builder.Configuration);
builder.Services.AddSingleton(clientIpTrust);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = 1; // Only trust the immediate reverse proxy

    // The container runs behind a reverse proxy on a Docker bridge network, so the proxy is never
    // on 127.0.0.1, and Request.Host must come from it (the CORS allow-list falls back to it). But
    // X-Forwarded-For drives the per-token IP whitelist and the rate limits, so the trusted set is
    // the configured reverse proxy, not everything (see ClientIpTrust.ApplyTo).
    ClientIpTrust.ApplyTo(options, clientIpTrust.Proxies,
        builder.Configuration["ForwardedHeaders:KnownNetworks"],
        builder.Configuration["ForwardedHeaders:KnownProxies"]);
});

// #78: the trusted caller's token only on the internal listener, its tenant headers only from there.
// The migrate step serves nothing and may be given only the database variables.
var portalTrust = migrateOnly ? new PortalTrustOptions() : PortalTrustOptions.From(builder.Configuration);
builder.Services.AddSingleton(portalTrust);
if (portalTrust.TrustedOnEveryPort)
{
    Console.Error.WriteLine(
        $"WARN: {PortalTrustOptions.TrustedCallerUserIdsVariable} is set but {PortalTrustOptions.InternalPortVariable} is empty: "
        + "the trusted token is accepted on every port, the public one included.");
}
if (portalTrust.UsesLegacyVariable)
{
    Console.Error.WriteLine(
        $"WARN: {PortalTrustOptions.LegacyUserIdsVariable} is deprecated, rename it to {PortalTrustOptions.TrustedCallerUserIdsVariable}.");
}
if (portalTrust.InternalPort is int internalPort)
{
    // A second listener next to whatever ASPNETCORE_URLS names (Kestrel's default otherwise).
    // It is for the Docker network: docker-compose only `expose`s it, never publishes it.
    builder.WebHost.UseUrls(PortalTrustOptions.WithInternalListener(
        builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey], internalPort));
}

// NOTE: Don't set ResourcesPath — SharedStrings.cs lives in DotNetSigningServer.Resources
// namespace, so the default baseName "{RootNamespace}.{TypeFullName-without-rootNS}" =
// "DotNetSigningServer.Resources.SharedStrings" matches the embedded .resx files directly.
// Adding ResourcesPath="Resources" would result in the wrong baseName
// "DotNetSigningServer.Resources.Resources.SharedStrings" and break all translations.
builder.Services.AddLocalization();
var privateServer = builder.Configuration.GetSection("PrivateServer")
    .Get<DotNetSigningServer.Options.PrivateServerOptions>() ?? new();
builder.Services.Configure<DotNetSigningServer.Options.PrivateServerOptions>(
    builder.Configuration.GetSection("PrivateServer"));
// P4 Backoffice integration (section P4Backoffice). Every module defaults to Off,
// and PrivateServer forces Off; see Services/Backoffice/BackofficeRegistration.cs.
builder.Services.AddP4BackofficeIntegration(builder.Configuration, privateServer);

builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add<DotNetSigningServer.Filters.NoIndexPrivatePagesFilter>();

        // Re-consent gate (P4Backoffice:Modules:Consents; does nothing while Off). Never on a
        // PrivateServer, where the backoffice integration is forced Off anyway.
        if (!privateServer.Enabled)
        {
            options.Filters.Add<DotNetSigningServer.Filters.RequireCurrentConsentFilter>();
        }

        // Applied as a convention rather than a filter: the routes are removed
        // rather than answered, so nothing can reach them at all.
        if (privateServer.Enabled)
        {
            options.Conventions.Add(new DotNetSigningServer.Conventions.PrivateServerConvention());
        }
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
builder.Services.AddScoped<DotNetSigningServer.Filters.ConsentVersionUnknownFilter>();
builder.Services.AddScoped<PdfVisualSigningService>();
builder.Services.AddScoped<PdfSigningService>();
builder.Services.AddScoped<PdfLtvService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IStripeCheckoutService, StripeCheckoutService>();
builder.Services.AddScoped<IApiAuthService, ApiAuthService>();
// SizeLimit counts entries: every cache.Set must carry Size = 1 (#74). Without a limit the
// token and consent caches grow with whatever a caller sends.
builder.Services.AddMemoryCache(o => o.SizeLimit = 10_000);
builder.Services.AddHttpClient("resend", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
// IEmailSender itself is registered by AddP4BackofficeIntegration (Modules:Email: On = outbox,
// otherwise ResendEmailSender directly).
builder.Services.AddSingleton<DotNetSigningServer.Services.Email.IEmailTemplateRenderer, DotNetSigningServer.Services.Email.EmailTemplateRenderer>();
builder.Services.AddScoped<PdfTemplateService>();
builder.Services.AddScoped<PdfConversionService>();
builder.Services.AddSingleton<ContentLimitGuard>();
builder.Services.AddSingleton<PdfSafetyGuard>();
builder.Services.AddScoped<IAutoRechargeService, AutoRechargeService>();
builder.Services.AddHostedService<PresignCleanupService>();
// Retention of Stripe webhook payloads and document metadata (#217).
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(builder.Services, TimeProvider.System);
builder.Services.AddHostedService<DataRetentionService>();
// Paid API operations: state, reservation, idempotent retries (#77, #82).
builder.Services.AddApiOperations(builder.Configuration);
// TSA deadline per authority (#77); the shared TSA client reads it per request.
PinnedIpTsaClient.RequestTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("Tsa:TimeoutSeconds") ?? 10);
// Own limit for authority calls (#72); empty = default from code, nonsense stops the start.
PinnedIpTsaClient.SetMaxConcurrentCalls(EnvValues.Int(builder.Configuration, "TSA_MAX_CONCURRENT_CALLS") ?? PinnedIpTsaClient.MaxConcurrentCalls);
// Circuit breaker per TSA host (#72). Invalid values throw here: startup fails closed rather
// than running with the protection silently off.
TsaCircuitBreaker.Default = new TsaCircuitBreaker(new TsaCircuitBreakerOptions
{
    FailureThreshold = builder.Configuration.GetValue<int?>("Tsa:Breaker:FailureThreshold") ?? 5,
    OpenSeconds = builder.Configuration.GetValue<int?>("Tsa:Breaker:OpenSeconds") ?? 30,
});
// ImageMagick (find-codes, #71): security policy and resource limits are process-wide and
// the policy only takes effect before the first image is touched, so it is set here. Throws
// when the policy did not take: Ghostscript on untrusted PDFs without it is not an option.
DotNetSigningServer.Services.CodeScanRaster.Configure(
    builder.Configuration.GetValue<long?>("Limits:FindCodesMagickMemoryBytes") ?? 0);
// PFX limits (#70) are read by a static helper that has no DI at the call site.
DotNetSigningServer.Services.PfxGuard.MaxBytes =
    builder.Configuration.GetValue<int?>("Limits:PfxMaxBytes") ?? DotNetSigningServer.Services.PfxGuard.MaxBytes;
DotNetSigningServer.Services.PfxGuard.MaxIterations =
    builder.Configuration.GetValue<long?>("Limits:PfxMaxIterations") ?? DotNetSigningServer.Services.PfxGuard.MaxIterations;
// PriceChangeMonitorService is registered by AddP4BackofficeIntegration, only while
// P4Backoffice:Modules:Pricing is not On (then the price.* events replace it).
builder.Services.AddSingleton<IAllowedOriginService, AllowedOriginService>();
builder.Services.AddSingleton<IIpWhitelistService, IpWhitelistService>();
// LokiClient is a singleton (it owns a batching queue + background flush
// timer) and pulls HttpClient instances from the factory by name.
builder.Services.AddHttpClient(nameof(LokiClient));
builder.Services.AddSingleton<LokiClient>();
// Ship all ILogger output (not just unhandled exceptions) to Loki.
builder.Services.AddSingleton<ILoggerProvider, DotNetSigningServer.Logging.LokiLoggerProvider>();
// The model gets 60 s; the caller's RequestAborted is passed on per call (#72).
builder.Services.AddHttpClient<TemplateAiService>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("osticket", client =>
{
    var timeout = builder.Configuration.GetValue<int?>("OsTicket:TimeoutSeconds") ?? 10;
    client.Timeout = TimeSpan.FromSeconds(timeout);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/support");
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "API", Version = "v1" });
    options.DocInclusionPredicate((docName, apiDesc) =>
    {
        if (apiDesc.ActionDescriptor is ControllerActionDescriptor cad)
        {
            return typeof(DotNetSigningServer.Controllers.ApiControllerBase).IsAssignableFrom(cad.ControllerTypeInfo.AsType());
        }
        return false;
    });
});
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection("Billing"));
// /api/webhooks/stripe is [AllowAnonymous]; the signature check is its only gate.
// The tracked appsettings.json ships WebhookSecret: "", and an empty HMAC key means
// an attacker can compute a valid Stripe-Signature and mint credits via a forged
// checkout.session.completed. Fail the deploy instead of serving unauthenticated.
builder.Services.AddOptions<StripeOptions>()
    .Bind(builder.Configuration.GetSection("Stripe"))
    .Validate(
        options => !builder.Environment.IsProduction()
                   || (!string.IsNullOrWhiteSpace(options.WebhookSecret)
                       && options.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal)),
        "Stripe:WebhookSecret must be configured with the signing secret (whsec_...) in production. "
        + "Set the Stripe__WebhookSecret environment variable.")
    .ValidateOnStart();
builder.Services.Configure<TokenOptions>(builder.Configuration.GetSection("Token"));
builder.Services.AddOptions<SessionSecurityOptions>()
    .Bind(builder.Configuration.GetSection(SessionSecurityOptions.SectionName))
    .PostConfigure(options =>
    {
        // docker-compose passes the raw names (#80); see EnvValues for why not Auth__*.
        options.ReauthMinutes = EnvValues.Int(builder.Configuration, "AUTH_REAUTH_MINUTES") ?? options.ReauthMinutes;
        options.OtpMaxFailedAttempts = EnvValues.Int(builder.Configuration, "AUTH_OTP_MAX_FAILED_ATTEMPTS") ?? options.OtpMaxFailedAttempts;
        options.AccountMaxAttempts = EnvValues.Int(builder.Configuration, "AUTH_ACCOUNT_MAX_ATTEMPTS") ?? options.AccountMaxAttempts;
        options.AccountWindowMinutes = EnvValues.Int(builder.Configuration, "AUTH_ACCOUNT_WINDOW_MINUTES") ?? options.AccountWindowMinutes;
    })
    .ValidateOnStart();
builder.Services.AddOptions<LimitsOptions>()
    .PostConfigure(options =>
        options.MaxApiTokensPerUser = EnvValues.Int(builder.Configuration, "MAX_API_TOKENS_PER_USER") ?? options.MaxApiTokensPerUser)
    .ValidateOnStart();
builder.Services.AddScoped<AuthAttemptLimiter>();
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection("Resend"));
builder.Services.Configure<LokiOptions>(builder.Configuration.GetSection("Loki"));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("AI"));
builder.Services.Configure<LimitsOptions>(builder.Configuration.GetSection("Limits"));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimits"));
DotNetSigningServer.Middleware.RateLimiting.Register(builder.Services, builder.Configuration);
// Slow-body protection (#72). Kestrel's defaults are 30 s for headers and 240 B/s for the
// body: a 40 MB upload may take days. 20 KB/s after a 10 s grace, headers within 15 s.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue<int?>("Limits:RequestHeadersTimeoutSeconds") ?? 15);
    kestrel.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
        bytesPerSecond: builder.Configuration.GetValue<int?>("Limits:MinRequestBodyBytesPerSecond") ?? 20 * 1024,
        gracePeriod: TimeSpan.FromSeconds(10));
});
builder.Services.Configure<EvidenceOptions>(builder.Configuration.GetSection("Evidence"));
builder.Services.Configure<OsTicketOptions>(builder.Configuration.GetSection("OsTicket"));
builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = DotNetSigningServer.Services.AuthenticationSchemeSelection.PolicySchemeName;
        options.DefaultAuthenticateScheme = DotNetSigningServer.Services.AuthenticationSchemeSelection.PolicySchemeName;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme(
        DotNetSigningServer.Services.AuthenticationSchemeSelection.PolicySchemeName,
        DotNetSigningServer.Services.AuthenticationSchemeSelection.PolicySchemeName,
        options =>
    {
        // Bearer (API key) only on /api/*; the web UI is cookie-only (#76).
        options.ForwardDefaultSelector = DotNetSigningServer.Services.AuthenticationSchemeSelection.Select;
    })
    .AddScheme<AuthenticationSchemeOptions, DotNetSigningServer.Services.ApiKeyAuthenticationHandler>(
        DotNetSigningServer.Services.ApiKeyAuthenticationHandler.SchemeName, _ => { })
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/SignIn";
        options.AccessDeniedPath = "/Account/Denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        // Every request: user exists, is active, the stamp (UpdatedAt) matches, and the Admin
        // role follows the database (#80). See CookieSessionValidator.
        options.Events.OnValidatePrincipal = DotNetSigningServer.Services.CookieSessionValidator.ValidateAsync;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// Health checks
builder.Services.AddHealthChecks();

#if DEBUG
// Dev only: Testcontainers is not part of the Release build, so it is not in the production image (#84).
PostgreSqlContainer? postgresContainer = null;
#endif

var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
                      policy =>
                      {
                          policy.AllowAnyHeader()
                                .AllowAnyMethod();
                      });
});

#if DEBUG
bool useLocalDb = builder.Configuration.GetValue<bool?>("UseLocalDb")
                  ?? builder.Environment.IsDevelopment();
#else
// Release has no Testcontainers: the Development default does not apply, only an explicit UseLocalDb=true is refused (#84).
bool useLocalDb = builder.Configuration.GetValue<bool?>("UseLocalDb") ?? false;
#endif

#if !DEBUG
if (useLocalDb)
{
    throw new InvalidOperationException("UseLocalDb needs a Debug build (it starts a Testcontainers PostgreSQL). Set ConnectionStrings__DefaultConnection or the DB_* variables.");
}
#else
if (useLocalDb)
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var hostDataPath = Path.Combine(currentDirectory, "postgres-data");
    Directory.CreateDirectory(hostDataPath);
    postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("signing_local")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithReuse(true)
        .WithAutoRemove(false)
        .WithBindMount(hostDataPath, "/var/lib/postgresql/data")
        .Build();

    await postgresContainer.StartAsync();

    var localConnectionString = EnsureSearchPath(postgresContainer.GetConnectionString(), "dotnet_signing");

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(localConnectionString, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dotnet_signing");
        }));
}
else
#endif
{
    var connectionString = DotNetSigningServer.Options.DatabaseUrl.ResolveRuntime(
        builder.Configuration, _ => BuildConnectionStringFromConfiguration(builder.Configuration));

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("A database connection string is required when UseLocalDb is false. Set DATABASE_URL, 'ConnectionStrings__DefaultConnection' or the DB_* environment variables.");
    }

    connectionString = EnsureSearchPath(connectionString, "dotnet_signing");

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
            npgsqlOptions.CommandTimeout(30);
            // dotnet_user has no CREATE on public; keep migration history in its own schema.
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dotnet_signing");
        }));
}

builder.Services.AddOptions<EvidenceOptions>()
    .Bind(builder.Configuration.GetSection("Evidence"))
    .PostConfigure(options =>
    {
        // Accept either raw PEM or a single-line base64 of the PEM (some platforms'
        // .env export mangles multi-line values, so base64 is the safe path).
        var pem = builder.Configuration["EVIDENCE_ENCRYPTION_CERTIFICATE_PEM"];
        var pemB64 = builder.Configuration["EVIDENCE_ENCRYPTION_CERTIFICATE_PEM_BASE64"];
        if (!string.IsNullOrEmpty(pemB64))
        {
            var cleaned = new string(pemB64.Where(c => !char.IsWhiteSpace(c)).ToArray());
            options.EncryptionCertificatePem = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
        }
        else if (!string.IsNullOrEmpty(pem))
        {
            options.EncryptionCertificatePem = pem;
        }
    });

builder.Services.AddOptions<BillingOptions>()
    .Bind(builder.Configuration.GetSection("Billing"))
    .PostConfigure(options =>
    {
        options.AttachmentDebitBypassKey = builder.Configuration["ATTACHMENT_DEBIT_BYPASS_KEY"] ?? options.AttachmentDebitBypassKey;
        options.GlobalConcurrencyLimit = EnvValues.Int(builder.Configuration, "GLOBAL_CONCURRENCY_LIMIT") ?? options.GlobalConcurrencyLimit;
        options.GlobalConcurrencyWaitMilliseconds = EnvValues.Int(builder.Configuration, "GLOBAL_CONCURRENCY_WAIT_MS") ?? options.GlobalConcurrencyWaitMilliseconds;
        // A value that makes no sense stops the server, it does not quietly switch the cap off.
        if (options.GlobalConcurrencyLimit is < 0)
        {
            throw new InvalidOperationException("GLOBAL_CONCURRENCY_LIMIT must be 0 (off) or a positive number.");
        }
        if (options.GlobalConcurrencyWaitMilliseconds < 0)
        {
            throw new InvalidOperationException("GLOBAL_CONCURRENCY_WAIT_MS must be 0 or a positive number of milliseconds.");
        }
        // #69: pools H and A, and the bounds of a user's queue.
        options.HeavyOperationsLimit = EnvValues.Int(builder.Configuration, "HEAVY_OPERATIONS_LIMIT") ?? options.HeavyOperationsLimit;
        options.AiOperationsLimit = EnvValues.Int(builder.Configuration, "AI_OPERATIONS_LIMIT") ?? options.AiOperationsLimit;
        options.UserQueueMaxSeconds = EnvValues.Int(builder.Configuration, "USER_QUEUE_MAX_SECONDS") ?? options.UserQueueMaxSeconds;
        options.UserQueueLengthFactor = EnvValues.Int(builder.Configuration, "USER_QUEUE_LENGTH_FACTOR") ?? options.UserQueueLengthFactor;
        if (options.HeavyOperationsLimit is < 0 or > BillingOptions.MaxPoolLimit)
        {
            throw new InvalidOperationException($"HEAVY_OPERATIONS_LIMIT must be 0 (off) or 1-{BillingOptions.MaxPoolLimit}.");
        }
        if (options.AiOperationsLimit is < 0 or > BillingOptions.MaxPoolLimit)
        {
            throw new InvalidOperationException($"AI_OPERATIONS_LIMIT must be 0 (off) or 1-{BillingOptions.MaxPoolLimit}.");
        }
        if (options.UserQueueMaxSeconds is < 0 or > BillingOptions.MaxQueueSeconds)
        {
            throw new InvalidOperationException($"USER_QUEUE_MAX_SECONDS must be 0-{BillingOptions.MaxQueueSeconds}.");
        }
        if (options.UserQueueLengthFactor is < 1 or > 100)
        {
            throw new InvalidOperationException("USER_QUEUE_LENGTH_FACTOR must be 1-100.");
        }
        if (options.ConcurrencyQueueTimeoutSeconds is < 0 or > BillingOptions.MaxQueueSeconds)
        {
            throw new InvalidOperationException($"Billing:ConcurrencyQueueTimeoutSeconds must be 0-{BillingOptions.MaxQueueSeconds}.");
        }
        options.ProductTagGraceUntil = EnvValues.Instant(builder.Configuration, "BILLING_PRODUCT_TAG_GRACE_UNTIL") ?? options.ProductTagGraceUntil;
    })
    .ValidateOnStart();
builder.Services.AddSingleton<CapacityPools>();
// On SIGTERM, requests already running may finish (#69): the longest deadline is 75 s. Stays
// under the container's stop_grace_period (135 s); the .NET default is 30 s.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(120));


var app = builder.Build();

// Fail fast on a missing public origin. Every absolute URL we emit — e-mail
// links, canonical tags, the sitemap — derives from it, so a deployment without
// it must not start rather than quietly ship dead links. The migrate step serves
// nothing and may be given only the database variables.
if (!migrateOnly)
{
    var appOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<AppOptions>>().Value;
    _ = appOptions.BaseUrl;

    // E-mail is optional — with no API key the sender just logs and skips. But a
    // key without a sender address is a misconfiguration that would fail one
    // message at a time, so catch it here instead.
    var resendOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ResendOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(resendOptions.ApiKey) && string.IsNullOrWhiteSpace(resendOptions.From))
    {
        throw new InvalidOperationException(
            "Resend:ApiKey is set but Resend:From is not. Set EMAIL_FROM to a sender on a domain "
            + "verified in Resend, e.g. \"Performance4PDF <noreply@send.example.com>\".");
    }
}

var appLogger = app.Services.GetRequiredService<ILogger<Program>>();

// #80: in Production the CORS list no longer falls back to the origins of every user's
// browser tokens. Without Cors:AllowedOrigins only the server's own origin passes.
if (!migrateOnly && app.Environment.IsProduction()
    && app.Services.GetRequiredService<IAllowedOriginService>() is AllowedOriginService origins
    && !origins.HasConfiguredCorsOrigins)
{
    appLogger.LogWarning(
        "Cors:AllowedOrigins is empty: browser calls to /api are accepted only from this server's own origin. "
        + "Set CORS_ALLOWED_ORIGINS (Cors__AllowedOrigins) to the sites that use browser tokens.");
}

// #106: without cloudflared in TRUSTED_CF_PROXIES the CF-Connecting-IP header is ignored, and
// behind the tunnel every visitor then has the tunnel's address — one sign-in limit (20 per
// 10 min) for everybody. That breaks sign-in rather than degrading it, so production refuses to
// start; a host with no tunnel in front says so with TRUSTED_CF_PROXIES=none.
if (!migrateOnly && app.Environment.IsProduction() && clientIpTrust.Cloudflare.IsEmpty && !clientIpTrust.Cloudflare.DeclaredNone)
{
    throw new InvalidOperationException(
        "TRUSTED_CF_PROXIES is empty: behind Cloudflare every client would share the tunnel's address and its rate limits. "
        + "Set it to the address / CIDR of cloudflared, or to 'none' when no tunnel is in front.");
}

#if DEBUG
if (postgresContainer != null)
{
    app.Lifetime.ApplicationStopping.Register(async () =>
    {
        appLogger.LogInformation("Application is stopping. Disposing of PostgreSQL Testcontainer...");
        await postgresContainer.DisposeAsync();
    });
}
#endif

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// The TCP peer before UseForwardedHeaders rewrites it: ClientIp trusts CF-Connecting-IP only
// when that peer is a configured proxy (#106).
app.Use(ClientIp.CaptureSocketPeer);
app.UseForwardedHeaders();

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
// HTTPS redirect is handled by the reverse proxy. The
// container only speaks HTTP internally, so UseHttpsRedirection would
// just log "Failed to determine the https port" on every request.

app.UseMiddleware<LokiExceptionMiddleware>();
app.UseMiddleware<BodySizeLimitMiddleware>();

// Moves a "/cs" style locale prefix into PathBase before anything else inspects
// the path, so every later check ("/Account", "/api", routing itself) sees the
// same path regardless of language. Must stay ahead of UseRequestLocalization.
app.UseMiddleware<DotNetSigningServer.Middleware.CulturePrefixMiddleware>();

// Swagger only in Development (#80): staging and test run a production build that is
// reachable from the internet, and the public reference lives at /api/docs.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
        options.RoutePrefix = "swagger";
    });
}

app.Use(async (context, next) =>
{
    // Apply CORS only to API routes
    if (context.Request.Path.HasValue && context.Request.Path.Value!.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        var origin = context.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            var originService = context.RequestServices.GetRequiredService<IAllowedOriginService>();
            if (!originService.IsOriginAllowed(origin, context))
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogWarning("CORS origin not allowed: {Origin}", origin);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Vary"] = "Origin";
            // No billing-bypass header (#78): only the portal sends it, server to server, never a browser.
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization,Content-Type,Idempotency-Key";
            context.Response.Headers["Access-Control-Expose-Headers"] = "X-Operation-Id,X-Operation-State,Idempotent-Replayed,Retry-After,X-Signature-Verified";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
        }

        if (string.Equals(context.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }

    await next();
});
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/ApiTokens", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.StartsWithSegments("/Account", StringComparison.OrdinalIgnoreCase))
    {
        var authResult = await context.AuthenticateAsync();
        if (!authResult.Succeeded)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Auth failed for {Path}. Cookies present: {HasCookie}", context.Request.Path, context.Request.Cookies.Any());
        }
    }
    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isSensitive = path.StartsWithSegments("/ApiTokens", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWithSegments("/Account", StringComparison.OrdinalIgnoreCase);
    await next();
    if (isSensitive && (context.Response.StatusCode == StatusCodes.Status403Forbidden || context.Response.StatusCode == StatusCodes.Status401Unauthorized))
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("Request {Path} returned {Status}. Authenticated: {Auth}, Cookie count: {CookieCount}, User: {UserId}", path, context.Response.StatusCode, context.User?.Identity?.IsAuthenticated, context.Request.Cookies?.Count ?? 0, context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "none");
    }
});
var supportedCultures = DotNetSigningServer.Services.CultureUrls.Supported;
app.UseRequestLocalization(options =>
{
    options.SetDefaultCulture(DotNetSigningServer.Services.CultureUrls.Default);
    options.AddSupportedCultures(supportedCultures);
    options.AddSupportedUICultures(supportedCultures);
    options.ApplyCurrentCultureToResponseHeaders = true;
    // The URL alone decides the language of a response. Cookie and Accept-Language
    // still steer which URL a visitor is sent to (see CulturePrefixMiddleware), but
    // letting them override the content here would mean /pricing rendering in Czech
    // while its canonical and hreflang tags claim it is the English page.
    options.RequestCultureProviders.Clear();
    options.RequestCultureProviders.Add(new DotNetSigningServer.Middleware.PathCultureProvider());
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Logo and stylesheet are content-stable and referenced from every page —
        // let browsers and CDNs hold on to them instead of refetching per request.
        ctx.Context.Response.Headers.CacheControl = "public, max-age=604800";
    },
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Before any body is read (#73): rate limits by IP / key / route, then 401 for what has no
// credentials, operator switches, and the block for keys that keep failing.
app.UseRateLimiter();
app.UseMiddleware<DotNetSigningServer.Middleware.ApiGateMiddleware>();

// Tenant and request id of a portal call into the log scope (#78); ignored from anybody else.
app.UseMiddleware<DotNetSigningServer.Middleware.PortalCallerScopeMiddleware>();

// Concurrency limiting runs AFTER authentication so ClaimsPrincipal carries the user id.
app.UseMiddleware<UserConcurrencyMiddleware>();

// Records UsageRecord rows for failed /api/* requests so users can see them on /Requests.
// Successful billable requests are still logged from ApiControllerBase.DebitUserAsync.
app.UseMiddleware<ApiRequestLoggingMiddleware>();

app.MapControllers();

// Health check endpoints
app.MapHealthChecks("/health");
app.MapHealthChecks("/healthz");

// Occupancy of the concurrency pools and queues (#69): counts only, served on the internal
// listener only (404 elsewhere, and everywhere while INTERNAL_API_PORT is empty).
app.MapGet(ConcurrencyDiagnostics.Path, (HttpContext context,
        PortalTrustOptions trust, CapacityPools pools) =>
    ConcurrencyDiagnostics.IsServedOn(trust, context.Connection.LocalPort)
        ? Results.Json(ConcurrencyDiagnostics.Snapshot(pools))
        : Results.NotFound());
{
    var pools = app.Services.GetRequiredService<CapacityPools>();
    var cpus = Environment.ProcessorCount;
    app.Logger.LogInformation(
        "Concurrency pools (0 = off): global {Global}, heavy {Heavy}, ai {Ai}, raster {Raster}; suggested for {Cpus} CPUs: "
        + "global {SuggestedGlobal}, heavy {SuggestedHeavy}, ai {SuggestedAi}",
        pools.Global.Limit, pools.Heavy.Limit, pools.Ai.Limit, CodeScanRaster.PoolSize, cpus,
        BillingOptions.SuggestedGlobalLimit(cpus), BillingOptions.SuggestedHeavyLimit(cpus), BillingOptions.SuggestedAiLimit);
}

// Loki test endpoint (Development only, #80: it is anonymous and writes to the log store)
if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/test-loki", async (LokiClient loki) =>
    {
        await loki.LogAsync("info", "Loki test: info message from dotnet-signing-server");
        await loki.LogAsync("warn", "Loki test: warning message from dotnet-signing-server");
        await loki.LogAsync("error", "Loki test: error message from dotnet-signing-server");
        // Force an immediate flush so the test result reflects real delivery.
        await loki.FlushAsync();
        return Results.Json(new { status = "ok", message = "Sent info, warn, and error to Loki" });
    });
}

// Migrations are a release step, not a side effect of every start (#85). Local dev
// (UseLocalDb) and an explicit Database:RunMigrationsOnStart (on-prem, single instance)
// keep the old behaviour; everything else only verifies nothing is pending.
var runMigrationsOnStart = useLocalDb
    || builder.Configuration.GetValue<bool>("Database:RunMigrationsOnStart");

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    if (migrateOnly)
    {
        // Migrate with the migration role (MIGRATION_DATABASE_URL, else Database:MigrationUser,
        // else the runtime one), long command timeout, no retries, then exit before anything
        // serves. The migration credentials are read only here, never by a serving process.
        var runtimeConnectionString = useLocalDb
            ? null
            : EnsureSearchPath(
                DotNetSigningServer.Options.DatabaseUrl.ResolveRuntime(
                    builder.Configuration, _ => BuildConnectionStringFromConfiguration(builder.Configuration))
                    ?? throw new InvalidOperationException("A database connection string is required."),
                "dotnet_signing");
        var migrationUrlConnectionString = DotNetSigningServer.Options.DatabaseUrl.ResolveMigration(builder.Configuration);
        var migrationUser = builder.Configuration["Database:MigrationUser"];
        using var migrationContext = runtimeConnectionString is null
            ? null
            : DotNetSigningServer.Services.DatabaseMigrator.CreateContext(
                migrationUrlConnectionString != null
                    ? EnsureSearchPath(migrationUrlConnectionString, "dotnet_signing")
                    : DotNetSigningServer.Services.DatabaseMigrator.BuildMigrationConnectionString(
                        runtimeConnectionString, migrationUser, builder.Configuration["Database:MigrationPassword"]));
        try
        {
            DotNetSigningServer.Services.DatabaseMigrator.EnsureSchemaAndMigrate(migrationContext ?? dbContext, logger);
            if (migrationContext != null && runtimeConnectionString != null)
            {
                DotNetSigningServer.Services.DatabaseMigrator.GrantRuntimeAccess(
                    migrationContext, new Npgsql.NpgsqlConnectionStringBuilder(runtimeConnectionString).Username, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed.");
            throw;
        }

        logger.LogInformation("Database migrations applied; exiting ({Switch}).", DotNetSigningServer.Services.DatabaseMigrator.MigrateArgument);
        return;
    }

    try
    {
        if (runMigrationsOnStart)
        {
            DotNetSigningServer.Services.DatabaseMigrator.EnsureSchemaAndMigrate(dbContext, logger);
        }
        else
        {
            DotNetSigningServer.Services.DatabaseMigrator.RequireNoPendingMigrations(dbContext, logger);
        }
    }
    catch (Exception ex)
    {
        // Fail fast so the orchestrator sees a failed deploy.
        // Serving against a half-migrated database silently breaks runtime queries.
        logger.LogCritical(ex, "Database is not ready — aborting startup.");
        throw;
    }

    // After the schema exists: a self-hosted installation has signing up switched
    // off, so without this there would be no way to obtain the first account.
    await DotNetSigningServer.Services.PrivateServerStartupChecks.EnsureAdministratorAsync(
        privateServer,
        builder.Configuration,
        dbContext,
        scope.ServiceProvider.GetRequiredService<DotNetSigningServer.Services.IAuthService>(),
        logger);
}

app.Run();

string? BuildConnectionStringFromConfiguration(ConfigurationManager configuration)
{
    var dbHost = configuration["DB_HOST"];
    var dbPort = configuration["DB_PORT"] ?? "5432";
    var dbName = configuration["DB_NAME"];
    var dbUser = configuration["DB_USER"];
    var dbPassword = configuration["DB_PASSWORD"];

    if (string.IsNullOrWhiteSpace(dbHost)
        || string.IsNullOrWhiteSpace(dbName)
        || string.IsNullOrWhiteSpace(dbUser)
        || string.IsNullOrWhiteSpace(dbPassword))
    {
        return null;
    }

    return $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPassword}";
}

static string EnsureSearchPath(string connectionString, string schema)
{
    var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
    {
        SearchPath = schema
    };
    return builder.ConnectionString;
}
