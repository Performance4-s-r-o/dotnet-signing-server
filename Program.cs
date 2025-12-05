using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication;
using Testcontainers.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Persist data protection keys so antiforgery/cookies survive restarts and container re-creations.
var dataProtectionPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH")
                        ?? Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // options.ForwardLimit = 2;
});

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<PdfSigningService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IStripeCheckoutService, StripeCheckoutService>();
builder.Services.AddScoped<IApiAuthService, ApiAuthService>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<PdfTemplateService>();
builder.Services.AddScoped<PdfConversionService>();
builder.Services.AddScoped<FlowPipelineService>();
builder.Services.AddSingleton<ContentLimitGuard>();
builder.Services.AddSingleton<IAllowedOriginService, AllowedOriginService>();
builder.Services.AddHttpClient<LokiClient>();
builder.Services.AddHttpClient<TemplateAiService>();
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection("Billing"));
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.Configure<TokenOptions>(builder.Configuration.GetSection("Token"));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<LokiOptions>(builder.Configuration.GetSection("Loki"));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("AI"));
builder.Services.Configure<LimitsOptions>(builder.Configuration.GetSection("Limits"));
builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/SignIn";
        options.AccessDeniedPath = "/Account/Denied";
        options.SlidingExpiration = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization();

PostgreSqlContainer? postgresContainer = null;

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

bool useLocalDb = builder.Configuration.GetValue<bool?>("UseLocalDb")
                  ?? builder.Environment.IsDevelopment();

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

    var localConnectionString = postgresContainer.GetConnectionString();

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(localConnectionString));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                         ?? BuildConnectionStringFromConfiguration(builder.Configuration);

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("A database connection string is required when UseLocalDb is false. Set 'ConnectionStrings__DefaultConnection' or the DB_* environment variables.");
    }

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(connectionString));
}

builder.Services.AddOptions<TimestampAuthorityOptions>()
    .Bind(builder.Configuration.GetSection("TimestampAuthority"))
    .PostConfigure(options =>
    {
        options.Url = builder.Configuration["TSA_URL"] ?? options.Url;
        options.Username = builder.Configuration["TSA_USERNAME"] ?? options.Username;
        options.Password = builder.Configuration["TSA_PASSWORD"] ?? options.Password;
    });


var app = builder.Build();

if (postgresContainer != null)
{
    app.Lifetime.ApplicationStopping.Register(async () =>
    {
        Console.WriteLine("Application is stopping. Disposing of PostgreSQL Testcontainer...");
        await postgresContainer.DisposeAsync();
    });
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseForwardedHeaders();
app.UseMiddleware<LokiExceptionMiddleware>();
app.UseMiddleware<BodySizeLimitMiddleware>();
app.UseMiddleware<RequestThrottlingMiddleware>();

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
                Console.WriteLine("CORS origin not allowed");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            Console.WriteLine($"Allowing CORS for origin: {origin}");
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Vary"] = "Origin";
            context.Response.Headers["Access-Control-Allow-Headers"] = context.Request.Headers["Access-Control-Request-Headers"].ToString() ?? "Authorization,Content-Type";
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
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        dbContext.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
    }
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
