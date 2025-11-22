using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<PdfSigningService>();

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
    var localDbPath = Path.Combine(AppContext.BaseDirectory, "signing-local.db");
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlite($"Data Source={localDbPath}"));
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

app.UseCors(MyAllowSpecificOrigins);
app.UseRouting();
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
