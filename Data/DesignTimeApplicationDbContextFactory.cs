using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace DotNetSigningServer.Data
{
    public class DesignTimeApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext(string[] args)
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            var useLocalDb = configuration.GetValue<bool?>("UseLocalDb")
                            ?? environment.Equals("Development", StringComparison.OrdinalIgnoreCase);

            if (useLocalDb)
            {
                // Use the project content root for the SQLite file to match the runtime location
                var localDbPath = Path.Combine(Directory.GetCurrentDirectory(), "signing-local.db");
                optionsBuilder.UseSqlite($"Data Source={localDbPath}");
            }
            else
            {
                var connectionString = configuration.GetConnectionString("DefaultConnection")
                                     ?? BuildConnectionStringFromConfiguration(configuration);

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("A database connection string is required when UseLocalDb is false.");
                }

                optionsBuilder.UseNpgsql(connectionString);
            }

            return new ApplicationDbContext(optionsBuilder.Options);
        }

        private static string? BuildConnectionStringFromConfiguration(IConfiguration configuration)
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
    }
}
