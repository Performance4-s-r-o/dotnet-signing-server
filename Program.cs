using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using System.Collections.Generic;
using System.Linq;

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
                          policy.WithOrigins("http://localhost:5173") // The origin of your React app
                                .AllowAnyHeader()
                                .AllowAnyMethod();
                      });
});

string envFile = ".env";
var envVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(envFile))
{
    string envFileContent = await File.ReadAllTextAsync(envFile);
    envVariables = envFileContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('='))
        .Where(parts => parts.Length > 0)
        .ToDictionary(parts => parts[0].Trim(), parts => parts.Length > 1 ? parts[1].Trim() : string.Empty, StringComparer.OrdinalIgnoreCase);
}

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
    if (envVariables.Count == 0)
    {
        throw new FileNotFoundException($"Environment file '{envFile}' not found. Please create it with the necessary configuration or enable the local development database.");
    }

    var connectionString = $"Host={envVariables["DB_HOST"]};Port={envVariables["DB_PORT"]};Database={envVariables["DB_NAME"]};Username={envVariables["DB_USER"]};Password={envVariables["DB_PASSWORD"]}";
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(connectionString));
}

builder.Services.AddOptions<TimestampAuthorityOptions>()
    .Bind(builder.Configuration.GetSection("TimestampAuthority"))
    .PostConfigure(options =>
    {
        if (envVariables.TryGetValue("TSA_URL", out var tsaUrl) && !string.IsNullOrWhiteSpace(tsaUrl))
        {
            options.Url = tsaUrl;
        }

        if (envVariables.TryGetValue("TSA_USERNAME", out var tsaUser) && !string.IsNullOrWhiteSpace(tsaUser))
        {
            options.Username = tsaUser;
        }

        if (envVariables.TryGetValue("TSA_PASSWORD", out var tsaPassword) && !string.IsNullOrWhiteSpace(tsaPassword))
        {
            options.Password = tsaPassword;
        }
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
