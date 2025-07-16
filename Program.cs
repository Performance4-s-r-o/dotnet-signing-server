using DotNetSigningServer.Data;
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
                          policy.WithOrigins("http://localhost:5173") // The origin of your React app
                                .AllowAnyHeader()
                                .AllowAnyMethod();
                      });
});

string envFile = ".env";
if (!File.Exists(envFile))
{
    throw new FileNotFoundException($"Environment file '{envFile}' not found. Please create it with the necessary configuration.");
}
string envFileContent = await File.ReadAllTextAsync(envFile);
var envVariables = envFileContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.Split('='))
    .ToDictionary(parts => parts[0].Trim(), parts => parts.Length > 1 ? parts[1].Trim() : string.Empty);
var connectionString = $"Host={envVariables["DB_HOST"]};Port={envVariables["DB_PORT"]};Database={envVariables["DB_NAME"]};Username={envVariables["DB_USER"]};Password={envVariables["DB_PASSWORD"]}";
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));


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
