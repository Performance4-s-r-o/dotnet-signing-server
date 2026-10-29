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
    public const int MinimumTokenLength = 43; // base64 of 32 random bytes

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
        if (EnvValues.IsUnresolvedPlaceholder(value) || value.Contains("{{", StringComparison.Ordinal))
        {
            problems.Add("PortalBootstrap:Token is an unresolved placeholder.");
        }
        if (KnownDefaults.Contains(value))
        {
            problems.Add("PortalBootstrap:Token is a well-known default value.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(value) < MinimumTokenLength)
        {
            problems.Add($"PortalBootstrap:Token is shorter than {MinimumTokenLength} characters (use `openssl rand -base64 32`).");
        }
        if (value.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            problems.Add("PortalBootstrap:Token contains whitespace or a quote.");
        }
        if (!LooksRandom(value))
        {
            problems.Add("PortalBootstrap:Token does not look random (too few character kinds, a repeated pattern or a sequence).");
        }
        return problems;
    }

    // Rough entropy guard: three character kinds, or long hex; no repeated pattern, no run of neighbours.
    private static bool LooksRandom(string v)
    {
        if (v.Distinct().Count() < 12) return false;
        for (var p = 1; p <= v.Length / 2; p++)
        {
            if (v.Length % p != 0) continue;
            var repeated = true;
            for (var i = p; i < v.Length && repeated; i++) repeated = v[i] == v[i - p];
            if (repeated) return false;
        }
        var steps = 0;
        for (var i = 1; i < v.Length; i++) if (v[i] - v[i - 1] == 1) steps++;
        if (steps * 2 > v.Length) return false;

        var kinds = (v.Any(char.IsLower) ? 1 : 0) + (v.Any(char.IsUpper) ? 1 : 0)
                    + (v.Any(char.IsDigit) ? 1 : 0) + (v.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        var hex = v.Length >= 64 && v.All(Uri.IsHexDigit);
        return kinds >= 3 || hex;
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

        // One hash, one account: a token that exists already (a hand-made portal account, a
        // revoked one) is never registered a second time.
        var sameHash = await db.ApiTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash).ToListAsync(cancellationToken);
        var email = UserEmails.Trim(AccountEmail);
        var existing = await db.Users.FindByEmailAsync(email, cancellationToken);
        var activeMatch = sameHash.FirstOrDefault(t => t.RevokedAt == null);
        if (activeMatch != null && (existing == null || activeMatch.UserId != existing.Id))
        {
            var hasOwnActive = existing != null && await db.ApiTokens.AnyAsync(
                t => t.UserId == existing.Id && t.Label == TokenLabel && t.RevokedAt == null, cancellationToken);
            if (hasOwnActive)
            {
                logger.LogWarning(
                    "[portal-bootstrap] the new value belongs to another account {UserId}; rotation not performed, the previous bootstrap token stays active",
                    activeMatch.UserId);
            }
            else
            {
                logger.LogInformation(
                    "[portal-bootstrap] the token already belongs to account {UserId}; bootstrap not performed", activeMatch.UserId);
            }
            return;
        }
        if (activeMatch == null && sameHash.Count > 0)
        {
            throw new InvalidOperationException(
                "PortalBootstrap refuses to start: this PORTAL_BOOTSTRAP_TOKEN value was revoked. Change the value.");
        }

        var user = existing;
        if (user != null && !user.IsServiceAccount)
        {
            throw new InvalidOperationException(
                "PortalBootstrap refuses to start: an ordinary account already uses the reserved portal address.");
        }
        if (user != null && (!user.IsActive || user.ErasedAt != null))
        {
            throw new InvalidOperationException(
                "PortalBootstrap refuses to start: the portal service account is deactivated or erased. Reactivate it in the database.");
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
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("[portal-bootstrap] created the portal service account {UserId}", user.Id);
            }
            catch (DbUpdateException)
            {
                // Another instance created it a moment ago: take that one, once.
                db.Entry(user).State = EntityState.Detached;
                user = await db.Users.FindByEmailAsync(email, cancellationToken);
                if (user is not { IsServiceAccount: true }) throw;
            }
        }

        if (activeMatch != null && activeMatch.Label != TokenLabel)
        {
            // The service account holds this value under another label: no second row with one hash.
            logger.LogInformation(
                "[portal-bootstrap] the token already exists on the service account as '{Label}'; nothing added", activeMatch.Label);
            return;
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
            // No prefix: it would leak 8 characters of the value.
            TokenPrefix = null,
            IsBrowserToken = false,
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "[portal-bootstrap] registered the bootstrap token (revoked {Revoked} previous)", active.Count);
    }

    private static (byte[] Hash, byte[] Salt, int Iterations) HashUnknownPassword() =>
        (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), AuthService.DefaultIterations);
}
