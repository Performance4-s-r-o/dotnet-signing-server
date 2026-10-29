using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace DotNetSigningServer.Services;

/// <summary>
/// Creates the portal's service account and registers its API token from configuration (#169),
/// so an installation needs no hand-made user and token. The value itself is never stored or
/// logged, nor does it appear in an error: only its HMAC (<see cref="ITokenService.HashToken"/>).
/// </summary>
public static class PortalBootstrap
{
    public const string AccountEmail = "portal@service.invalid";
    public const string TokenLabel = "bootstrap";
    public const int MinimumTokenLength = 32;

    private static readonly HashSet<string> KnownDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "change-this-secret", "changeme", "change-me", "secret", "password", "token",
        "sign-api-token", "your-token-here", "test", "demo",
    };

    /// <returns>Every problem found. Never contains the value.</returns>
    public static IReadOnlyList<string> Validate(string? token)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(token)) return problems;

        var value = token.Trim();
        if (KnownDefaults.Contains(value))
        {
            problems.Add("PortalBootstrap:Token is a well-known default value.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(value) < MinimumTokenLength)
        {
            problems.Add($"PortalBootstrap:Token is shorter than {MinimumTokenLength} bytes.");
        }
        if (value.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            problems.Add("PortalBootstrap:Token contains whitespace or a quote.");
        }
        if (value.Distinct().Count() < 8)
        {
            problems.Add("PortalBootstrap:Token has too little variety (fewer than 8 distinct characters).");
        }
        return problems;
    }

    /// <summary>
    /// Idempotent. The account is created once; the token is replaced only when the stored
    /// hash no longer matches the configured value (rotation, or a changed Token:Secret).
    /// </summary>
    public static async Task EnsureAsync(
        PortalBootstrapOptions options,
        PrivateServerOptions privateServer,
        ITokenService tokenService,
        ApplicationDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Token)) return;

        var problems = Validate(options.Token);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "PortalBootstrap refuses to start:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        // The same form the authentication handler looks the presented token up by.
        var token = ApiAuthService.NormalizeToken(options.Token.Trim());
        var hash = tokenService.HashToken(token);

        var email = UserEmails.Trim(AccountEmail);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user != null && !user.IsServiceAccount)
        {
            throw new InvalidOperationException(
                "PortalBootstrap refuses to start: an ordinary account already uses the reserved portal address.");
        }

        if (user == null)
        {
            // A random hash nobody holds the password for; the sign-in paths refuse the flag anyway.
            var (pwHash, pwSalt, pwIterations) = HashUnknownPassword();
            user = new User
            {
                Email = email,
                PasswordHash = pwHash,
                PasswordSalt = pwSalt,
                PasswordIterations = pwIterations,
                IsActive = true,
                EmailVerified = false,
                IsServiceAccount = true,
                TrustedCaller = true,
                SealAllowed = true,
                // No credit limit on a private server; the existing enterprise switch does that.
                IsEnterprise = privateServer.Enabled,
                EnterpriseEnabledAt = privateServer.Enabled ? DateTimeOffset.UtcNow : null,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[portal-bootstrap] created the portal service account {UserId}", user.Id);
        }

        var active = await db.ApiTokens
            .Where(t => t.UserId == user.Id && t.Label == TokenLabel && t.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (active.Any(t => CryptographicOperations.FixedTimeEquals(t.TokenHash, hash))) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var old in active) old.RevokedAt = now;
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = TokenLabel,
            TokenHash = hash,
            TokenPrefix = token.Length >= 8 ? token[..8] : token,
            IsBrowserToken = false,
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "[portal-bootstrap] registered the bootstrap token (revoked {Revoked} previous)", active.Count);
    }

    private static (byte[] Hash, byte[] Salt, int Iterations) HashUnknownPassword() =>
        (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), AuthService.DefaultIterations);
}
