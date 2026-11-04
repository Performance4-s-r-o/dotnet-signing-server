using System.Security.Cryptography;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Ops;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Provisioning;

/// <summary>How a provisioning call ended; each maps to one answer of the API.</summary>
public enum ProvisionOutcome
{
    /// <summary>201: a new account with the given token.</summary>
    Created,
    /// <summary>200: the account exists and the token is its active one (a retry).</summary>
    Replayed,
    /// <summary>409 <c>ACCOUNT_EXISTS</c>: the account exists with another token, or the token belongs elsewhere.</summary>
    Conflict,
    /// <summary>409 <c>PARENT_NOT_ENTERPRISE</c>.</summary>
    ParentNotEnterprise,
    /// <summary>429 <c>PROVISIONING_LIMIT</c>: calls per minute or accounts per day.</summary>
    Limited,
    /// <summary>400 <c>EXTERNAL_ID_INVALID</c>.</summary>
    InvalidExternalId,
    /// <summary>400 <c>LABEL_INVALID</c>.</summary>
    InvalidLabel,
    /// <summary>400 <c>TOKEN_WEAK</c>.</summary>
    WeakToken,
    /// <summary>403 <c>TOKEN_SCOPE</c>: the caller cannot be a parent.</summary>
    NotAParent,
}

/// <summary>The caller of the provisioning API: the provisioning token and its account.</summary>
public sealed record ProvisioningCaller(Guid UserId, Guid TokenId, string? ClientIp);

/// <param name="Account">The provisioned account for Created / Replayed, with its parent loaded.</param>
public sealed record ProvisionResult(ProvisionOutcome Outcome, User? Account = null, int RetryAfterSeconds = 0);

/// <summary>
/// Creates the integrator's provisioned accounts (#131). The integrator generates each account's
/// token and sends it; only its HMAC is stored, so a repeated call with the same token is answered
/// 200 and the server never hands out a token for an account that exists. Idempotent by
/// (parent, external id). Every call leaves a row in <see cref="AccountProvisioningEvent"/>.
/// </summary>
public sealed class AccountProvisioner
{
    public const string TokenLabel = "provisioned";
    public const string LimitAlertKey = "provisioning-limit";
    public const string UnusualAlertKey = "provisioning-unusual";

    private readonly ApplicationDbContext _db;
    private readonly ITokenService _tokens;
    private readonly AuthAttemptLimiter _limiter;
    private readonly ProvisioningOptions _options;
    private readonly IOpsAlerter _alerter;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountProvisioner> _logger;

    public AccountProvisioner(
        ApplicationDbContext db,
        ITokenService tokens,
        AuthAttemptLimiter limiter,
        ProvisioningOptions options,
        IOpsAlerter alerter,
        TimeProvider time,
        ILogger<AccountProvisioner> logger)
    {
        _db = db;
        _tokens = tokens;
        _limiter = limiter;
        _options = options;
        _alerter = alerter;
        _time = time;
        _logger = logger;
    }

    /// <summary>1–64 characters of <c>[A-Za-z0-9._:-]</c>: a tenant UUID, or <c>platform</c>.</summary>
    public static bool IsValidExternalId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > User.MaxExternalIdLength) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return false;
        }
        return true;
    }

    /// <summary>Empty, or up to 128 characters without control characters.</summary>
    public static bool IsValidLabel(string? value) =>
        value == null || (value.Length <= User.MaxLabelLength && !value.Any(char.IsControl));

    /// <summary>The account's token as stored: HMAC of the normalised value.</summary>
    public byte[] HashOf(string token) => _tokens.HashToken(ApiAuthService.NormalizeToken(token.Trim()));

    /// <summary>Counts one call of <paramref name="tokenId"/>; true when it is over the per-minute limit.</summary>
    public Task<bool> IsRateLimitedAsync(string scope, Guid id, CancellationToken ct) =>
        _limiter.IsLimitedAsync($"{scope}:{id:N}", _options.CallsPerMinute, TimeSpan.FromMinutes(1), ct);

    public async Task<ProvisionResult> ProvisionAsync(
        ProvisioningCaller caller, string? externalId, string? label, string? apiToken, CancellationToken ct)
    {
        var result = await DecideAsync(caller, externalId?.Trim(), string.IsNullOrWhiteSpace(label) ? null : label.Trim(), apiToken, ct);
        await RecordAsync(caller, "create", OutcomeName(result.Outcome), IsValidExternalId(externalId?.Trim()) ? externalId!.Trim() : null,
            result.Account?.Id, ct);
        return result;
    }

    private async Task<ProvisionResult> DecideAsync(
        ProvisioningCaller caller, string? externalId, string? label, string? apiToken, CancellationToken ct)
    {
        var parent = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == caller.UserId, ct);
        if (parent == null || parent.ParentUserId != null || !parent.IsActive || parent.ErasedAt != null)
        {
            // One level only: a provisioned account never provisions.
            return new ProvisionResult(ProvisionOutcome.NotAParent);
        }

        if (await IsRateLimitedAsync("provision", caller.TokenId, ct))
        {
            return new ProvisionResult(ProvisionOutcome.Limited, RetryAfterSeconds: 60);
        }

        if (!IsValidExternalId(externalId)) return new ProvisionResult(ProvisionOutcome.InvalidExternalId);
        if (!IsValidLabel(label)) return new ProvisionResult(ProvisionOutcome.InvalidLabel);
        if (string.IsNullOrWhiteSpace(apiToken) || PortalBootstrap.Validate(apiToken, "apiToken").Count > 0)
        {
            return new ProvisionResult(ProvisionOutcome.WeakToken);
        }

        // Without Enterprise every provisioned account would spend credits it does not have (Q1).
        if (!parent.IsEnterprise) return new ProvisionResult(ProvisionOutcome.ParentNotEnterprise);

        var hash = HashOf(apiToken);
        var existing = await DecideExistingAsync(parent, externalId!, hash, ct);
        if (existing != null) return existing;

        // Any token with this value, live or revoked, of any account: never a second meaning.
        if (await _db.ApiTokens.AnyAsync(t => t.TokenHash == hash, ct))
        {
            return new ProvisionResult(ProvisionOutcome.Conflict);
        }

        var now = _time.GetUtcNow();
        if (_options.MaxAccountsPerDay > 0)
        {
            var today = await _db.Users.CountAsync(u => u.ParentUserId == parent.Id && u.CreatedAt >= now.AddDays(-1), ct);
            if (today >= _options.MaxAccountsPerDay)
            {
                _logger.LogError("Provisioning refused for parent {ParentId}: {Count} accounts in 24 h reached the limit {Limit}",
                    parent.Id, today, _options.MaxAccountsPerDay);
                _alerter.Alert(LimitAlertKey,
                    $"Provisioning refused: parent {parent.Id} created {today} accounts in 24 h (limit {_options.MaxAccountsPerDay}).");
                return new ProvisionResult(ProvisionOutcome.Limited, RetryAfterSeconds: 3600);
            }
        }

        var (pwHash, pwSalt) = (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        var id = Guid.NewGuid();
        var account = new User
        {
            Id = id,
            Email = $"child-{id:N}@service.invalid",
            PasswordHash = pwHash,
            PasswordSalt = pwSalt,
            PasswordIterations = AuthService.DefaultIterations,
            IsActive = true,
            EmailVerified = false,
            EmailNotificationsEnabled = false,
            IsServiceAccount = true,
            // Inherited from the parent, never its own (EffectiveEnterprise, EffectiveSealAllowed).
            IsEnterprise = false,
            SealAllowed = false,
            CreditsRemaining = 0,
            ParentUserId = parent.Id,
            ExternalId = externalId,
            Label = label,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(account);
        _db.ApiTokens.Add(new ApiToken
        {
            UserId = id,
            Label = TokenLabel,
            TokenHash = hash,
            // No prefix: it would store 8 characters of the integrator's value.
            TokenPrefix = null,
            IsBrowserToken = false,
            IsProvisioningToken = false,
            CreatedAt = now,
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (CreditGrants.IsUniqueViolation(ex))
        {
            // A concurrent call created it: decide against what is there now.
            _db.ChangeTracker.Clear();
            return await DecideExistingAsync(parent, externalId!, hash, ct) ?? new ProvisionResult(ProvisionOutcome.Conflict);
        }

        _db.ChangeTracker.Clear();
        _logger.LogInformation("Provisioned account {AccountId} for parent {ParentId}", id, parent.Id);
        await AlertWhenUnusualAsync(parent.Id, now, ct);

        account.Parent = parent;
        return new ProvisionResult(ProvisionOutcome.Created, account);
    }

    /// <summary>The live account of (parent, external id): a replay or a conflict; null when there is none.</summary>
    private async Task<ProvisionResult?> DecideExistingAsync(User parent, string externalId, byte[] hash, CancellationToken ct)
    {
        var child = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.ParentUserId == parent.Id && u.ExternalId == externalId && u.ErasedAt == null, ct);
        if (child == null) return null;

        var now = _time.GetUtcNow();
        var activeHashes = await _db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == child.Id && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Select(t => t.TokenHash)
            .ToListAsync(ct);
        if (!activeHashes.Any(h => CryptographicOperations.FixedTimeEquals(h, hash)))
        {
            return new ProvisionResult(ProvisionOutcome.Conflict);
        }
        child.Parent = parent;
        return new ProvisionResult(ProvisionOutcome.Replayed, child);
    }

    private async Task AlertWhenUnusualAsync(Guid parentId, DateTimeOffset now, CancellationToken ct)
    {
        if (_options.AlertAccountsPerHour <= 0) return;
        var lastHour = await _db.Users.CountAsync(u => u.ParentUserId == parentId && u.CreatedAt >= now.AddHours(-1), ct);
        if (lastHour < _options.AlertAccountsPerHour) return;
        _logger.LogWarning("Parent {ParentId} provisioned {Count} accounts in the last hour", parentId, lastHour);
        _alerter.Alert(UnusualAlertKey,
            $"Unusual provisioning: parent {parentId} created {lastHour} accounts in the last hour (alert at {_options.AlertAccountsPerHour}).");
    }

    /// <summary>The parent's provisioned accounts, erased ones included; never a token.</summary>
    public async Task<IReadOnlyList<User>> ListAsync(ProvisioningCaller caller, string? externalId, int limit, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().Where(u => u.ParentUserId == caller.UserId);
        if (!string.IsNullOrEmpty(externalId)) query = query.Where(u => u.ExternalId == externalId);
        var rows = await query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Take(limit).ToListAsync(ct);
        await RecordAsync(caller, "list", AccountProvisioningOutcomes.Listed, IsValidExternalId(externalId) ? externalId : null, null, ct);
        return rows;
    }

    public Task RecordListRefusalAsync(ProvisioningCaller caller, string outcome, CancellationToken ct) =>
        RecordAsync(caller, "list", outcome, null, null, ct);

    private async Task RecordAsync(ProvisioningCaller caller, string operation, string outcome, string? externalId, Guid? childId, CancellationToken ct)
    {
        _db.AccountProvisioningEvents.Add(new AccountProvisioningEvent
        {
            CreatedAt = _time.GetUtcNow(),
            TokenId = caller.TokenId,
            CallerUserId = caller.UserId,
            ChildUserId = childId,
            ExternalId = externalId,
            Operation = operation,
            Outcome = outcome,
            ClientIp = Truncate(caller.ClientIp, 64),
        });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The gate refused a token outside its scope on, or with, the provisioning API (#131). Best
    /// effort: the refusal stands whether or not the row is written.
    /// </summary>
    public static async Task RecordScopeRefusalAsync(HttpContext context, Guid? userId)
    {
        try
        {
            var db = context.RequestServices.GetService<ApplicationDbContext>();
            if (db == null) return;
            db.AccountProvisioningEvents.Add(new AccountProvisioningEvent
            {
                CreatedAt = DateTimeOffset.UtcNow,
                TokenId = SessionClaims.TokenIdOf(context.User),
                CallerUserId = userId,
                Operation = "scope",
                Outcome = AccountProvisioningOutcomes.RefusedScope,
                ClientIp = Truncate(Middleware.ClientIp.AddressOf(context)?.ToString(), 64),
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<AccountProvisioner>()
                .LogWarning(ex, "Could not record a provisioning scope refusal");
        }
    }

    public static string OutcomeName(ProvisionOutcome outcome) => outcome switch
    {
        ProvisionOutcome.Created => AccountProvisioningOutcomes.Created,
        ProvisionOutcome.Replayed => AccountProvisioningOutcomes.Replayed,
        ProvisionOutcome.Conflict => AccountProvisioningOutcomes.Conflict,
        ProvisionOutcome.ParentNotEnterprise => AccountProvisioningOutcomes.RefusedParent,
        ProvisionOutcome.NotAParent => AccountProvisioningOutcomes.RefusedScope,
        ProvisionOutcome.Limited => AccountProvisioningOutcomes.RefusedLimit,
        _ => AccountProvisioningOutcomes.Invalid,
    };

    private static string? Truncate(string? value, int max) =>
        value == null ? null : value.Length <= max ? value : value[..max];
}
