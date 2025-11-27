using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

[Authorize]
public class ApiTokensController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly ILogger<ApiTokensController> _logger;

    public ApiTokensController(ApplicationDbContext dbContext, ITokenService tokenService, ILogger<ApiTokensController> logger)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _logger = logger;
    }

    [HttpGet("/ApiTokens")]
    public async Task<IActionResult> Index()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var tokens = await _dbContext.ApiTokens
            .Include(t => t.User)
            .AsNoTracking()
            .Where(t => t.UserId == userId.Value)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return View(tokens);
    }

    [HttpPost("/ApiTokens")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string label, DateTimeOffset? expiresAt = null, string usageType = "server", string? allowedOrigins = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var user = await _dbContext.Users.FindAsync(userId.Value);
        if (user == null)
        {
            TempData["Error"] = "User not found.";
            return RedirectToAction(nameof(Index));
        }

        var isBrowser = string.Equals(usageType, "web", StringComparison.OrdinalIgnoreCase);
        var normalizedOrigins = NormalizeOrigins(allowedOrigins, includeLocalhost: true)
            .Where(o => o.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || o.StartsWith("http://localhost") || o.StartsWith("http://127.0.0.1") || o.StartsWith("https://localhost") || o.StartsWith("https://127.0.0.1"))
            .Take(10)
            .ToList();

        if (isBrowser && normalizedOrigins.Count == 0)
        {
            TempData["Error"] = "At least one valid origin (https or localhost) is required for browser tokens.";
            return RedirectToAction(nameof(Index));
        }

        var (plaintext, hash, _) = _tokenService.IssueToken(user, label, expiresAt);
        var token = new ApiToken
        {
            UserId = user.Id,
            Label = label,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            IsBrowserToken = isBrowser,
            AllowedOrigins = isBrowser ? string.Join("\n", normalizedOrigins) : null
        };

        _dbContext.ApiTokens.Add(token);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(DotNetSigningServer.Logging.LoggingEvents.TokenCreated, "Token created for user {UserId} label {Label} browser {Browser}", user.Id, label, isBrowser);
        TempData["NewToken"] = plaintext;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/ApiTokens/{id}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var token = await _dbContext.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value);
        if (token == null)
        {
            TempData["Error"] = "Token not found.";
            return RedirectToAction(nameof(Index));
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation(DotNetSigningServer.Logging.LoggingEvents.TokenRevoked, "Token {TokenId} revoked for user {UserId}", id, userId);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/ApiTokens/{id}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var token = await _dbContext.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value);
        if (token == null)
        {
            TempData["Error"] = "Token not found.";
            return RedirectToAction(nameof(Index));
        }

        _dbContext.ApiTokens.Remove(token);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation(DotNetSigningServer.Logging.LoggingEvents.TokenDeleted, "Token {TokenId} deleted for user {UserId}", id, userId);
        TempData["Info"] = "Token deleted.";
        return RedirectToAction(nameof(Index));
    }

    private static IEnumerable<string> NormalizeOrigins(string? rawOrigins, bool includeLocalhost)
    {
        var origins = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawOrigins))
        {
            var split = rawOrigins.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            origins.AddRange(split);
        }

        if (includeLocalhost)
        {
            origins.AddRange(new[]
            {
                "http://localhost",
                "https://localhost",
                "http://127.0.0.1",
                "https://127.0.0.1"
            });
        }

        return origins
            .Select(o =>
            {
                if (!Uri.TryCreate(o, UriKind.Absolute, out var uri))
                {
                    return null;
                }
                var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
                return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            })
            .Where(o => o != null)
            .Select(o => o!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
