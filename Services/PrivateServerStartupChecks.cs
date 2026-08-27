using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Bringing up a self-hosted installation.
///
/// Two things have to be true that are not true of the hosted service: there has
/// to be a way to get the first account, since signing up is switched off, and
/// that account must not be openable by a string somebody read in a manual.
///
/// Both are enforced by refusing to start rather than by logging. Nobody reads
/// the log of a server that came up, and each of these produces an installation
/// that works perfectly until somebody finds it.
/// </summary>
public static class PrivateServerStartupChecks
{
    /// <summary>
    /// Passwords from templates, examples and tutorials. An appliance shipped
    /// with one of these is where a good share of that category of CVE comes
    /// from — one published string that opens every installation at once.
    /// </summary>
    private static readonly HashSet<string> KnownDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "password", "changeme", "change-me", "secret", "letmein",
        "admin123", "password123", "123456", "P@ssw0rd", "test", "demo",
    };

    private const int MinimumPasswordLength = 12;

    /// <returns>Every problem found, so one restart surfaces all of them.</returns>
    public static IReadOnlyList<string> Validate(
        PrivateServerOptions options,
        string? adminEmail,
        string? adminPassword,
        bool anyUserExists)
    {
        var problems = new List<string>();
        if (!options.Enabled) return problems;

        // Once an account exists the credentials have done their job and may be
        // taken back out of the environment — leaving a password in there for the
        // life of the installation is worse than needing it once.
        if (anyUserExists) return problems;

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            problems.Add(
                "No administrator exists and signing up is switched off, so nothing could create one. " +
                "Set SeedAdmin:Email and SeedAdmin:Password for the first start.");
            return problems;
        }

        if (KnownDefaults.Contains(adminPassword.Trim()))
        {
            problems.Add(
                "The administrator password is a well-known default. " +
                "Every installation shipped with the same one is opened by the same string.");
        }
        else if (adminPassword.Trim().Length < MinimumPasswordLength)
        {
            problems.Add(
                $"The administrator password is shorter than {MinimumPasswordLength} characters. " +
                "This account manages the API keys to a signing server.");
        }

        if (!adminEmail.Contains('@'))
        {
            problems.Add("SeedAdmin:Email is not an email address.");
        }

        return problems;
    }

    /// <summary>
    /// Checks the configuration and creates the first administrator if there is
    /// none. Runs once at startup, before the server accepts anything.
    /// </summary>
    public static async Task EnsureAdministratorAsync(
        PrivateServerOptions options,
        IConfiguration configuration,
        ApplicationDbContext dbContext,
        IAuthService authService,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return;

        var email = (configuration["SeedAdmin:Email"] ?? configuration["SEED_ADMIN_EMAIL"])?.Trim();
        var password = configuration["SeedAdmin:Password"] ?? configuration["SEED_ADMIN_PASSWORD"];
        var anyUser = await dbContext.Users.AnyAsync(cancellationToken);

        var problems = Validate(options, email, password, anyUser);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "This installation refuses to start:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        if (anyUser) return;

        var (hash, salt, iterations) = authService.HashPassword(password!);
        dbContext.Users.Add(new User
        {
            Email = email!,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            IsActive = true,
            // Nothing will ever send a verification message here: a self-hosted
            // installation has no outbound mail of its own, and this account was
            // put in by whoever installed the server.
            EmailVerified = true,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("[private-server] created the first administrator ({Email})", email);
    }
}
