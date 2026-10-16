using Microsoft.EntityFrameworkCore;
using DotNetSigningServer.Data;

namespace DotNetSigningServer.Services;

/// <summary>
/// What a browser token may do (#80). Its Origin lock only binds a browser: any other client
/// sends whatever Origin it likes, so a browser token that leaks from a web page is assumed to be
/// usable from anywhere. It is therefore limited to the signing flow — presign, sign, the state
/// of an operation it started, the capability list — and optionally to a credit budget. Templates,
/// fill, sealing, PFX, AI and everything else answer 403 <c>TOKEN_SCOPE</c>.
///
/// Server tokens (the portal's <c>SIGN_API_TOKEN</c> among them) are not affected.
/// </summary>
public static class BrowserTokenScope
{
    public const string ScopeErrorCode = "TOKEN_SCOPE";
    public const string CreditLimitErrorCode = "TOKEN_CREDIT_LIMIT";

    private static readonly (string Method, string Path, bool Prefix)[] Allowed =
    {
        ("POST", "/api/presign", false),
        ("POST", "/api/sign", false),
        ("GET", "/api/operations/", true),
        ("GET", "/api/capabilities", false),
        ("OPTIONS", "/api/", true),
    };

    public static bool IsAllowed(string method, PathString path)
    {
        var value = (path.Value ?? string.Empty).TrimEnd('/');
        foreach (var (m, p, prefix) in Allowed)
        {
            if (!string.Equals(method, m, StringComparison.OrdinalIgnoreCase)) continue;
            if (prefix
                    ? (path.Value ?? string.Empty).StartsWith(p, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(value, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reserves <paramref name="credits"/> on the token's budget in one statement; false when it
    /// would go over. A token without a limit always passes (and still counts what it spends).
    /// </summary>
    public static async Task<bool> TryReserveAsync(ApplicationDbContext db, Guid tokenId, int credits, CancellationToken cancellationToken = default)
    {
        if (credits <= 0) return true;
        var updated = await db.ApiTokens
            .Where(t => t.Id == tokenId && (t.CreditLimit == null || t.CreditsUsed + credits <= t.CreditLimit))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreditsUsed, t => t.CreditsUsed + credits), cancellationToken);
        return updated == 1;
    }

    /// <summary>Gives back a reservation whose work did not complete.</summary>
    public static async Task ReleaseAsync(ApplicationDbContext db, Guid tokenId, int credits)
    {
        if (credits <= 0) return;
        await db.ApiTokens
            .Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreditsUsed, t => t.CreditsUsed >= credits ? t.CreditsUsed - credits : 0));
    }
}

/// <summary>
/// A browser token's budget reservation for one operation (#80). Disposed without
/// <see cref="Commit"/> it gives the credits back — whatever ended the operation: a failure, an
/// abort, a lost lease or an exception from completing it. Reserved and released with
/// <see cref="CancellationToken.None"/>: an aborted request must neither skip the reservation
/// halfway nor keep credits it never used.
/// </summary>
public sealed class BrowserTokenBudget : IAsyncDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger? _logger;
    private bool _settled;

    public Guid TokenId { get; }
    public int Credits { get; }

    private BrowserTokenBudget(ApplicationDbContext db, Guid tokenId, int credits, ILogger? logger)
    {
        _db = db;
        TokenId = tokenId;
        Credits = credits;
        _logger = logger;
    }

    /// <summary>The reservation, or null when the token's limit does not allow it.</summary>
    public static async Task<BrowserTokenBudget?> TryReserveAsync(ApplicationDbContext db, Guid tokenId, int credits, ILogger? logger = null) =>
        await BrowserTokenScope.TryReserveAsync(db, tokenId, credits, CancellationToken.None)
            ? new BrowserTokenBudget(db, tokenId, credits, logger)
            : null;

    /// <summary>The operation completed: the credits stay spent.</summary>
    public void Commit() => _settled = true;

    public async ValueTask DisposeAsync()
    {
        if (_settled) return;
        _settled = true;
        try
        {
            await BrowserTokenScope.ReleaseAsync(_db, TokenId, Credits);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not release the budget of token {TokenId}", TokenId);
        }
    }
}
