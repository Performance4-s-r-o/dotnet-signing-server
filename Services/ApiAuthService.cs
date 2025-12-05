using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace DotNetSigningServer.Services;

public class ApiAuthService : IApiAuthService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly IAllowedOriginService _allowedOriginService;

    public ApiAuthService(ApplicationDbContext dbContext, ITokenService tokenService, IAllowedOriginService allowedOriginService)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _allowedOriginService = allowedOriginService;
    }

    public async Task<User?> ValidateTokenAsync(string authorizationHeader, string? originHeader = null)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        token = NormalizeToken(token);

        var now = DateTimeOffset.UtcNow;

        var candidates = await _dbContext.ApiTokens
            .Include(t => t.User)
            .Where(t => t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .ToListAsync();

        var apiToken = candidates.FirstOrDefault(t => _tokenService.VerifyToken(token, t.TokenHash));
        if (apiToken == null)
        {
            return null;
        }

        if (apiToken.IsBrowserToken)
        {
            var origin = originHeader?.Trim();
            if (string.IsNullOrWhiteSpace(origin))
            {
                return null;
            }

            if (!_allowedOriginService.IsOriginAllowedForToken(origin, apiToken))
            {
                return null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(originHeader) && !_allowedOriginService.IsLocalOrigin(originHeader))
        {
            // Server tokens must not be used from arbitrary browser origins.
            return null;
        }

        return apiToken.User;
    }

    private static string NormalizeToken(string token)
    {
        var trimmed = token.Trim().Trim('"');
        // Replace common copy/paste issues where + becomes space
        trimmed = trimmed.Replace(' ', '+');
        // Base64 padding fixup if user trimmed it
        int mod4 = trimmed.Length % 4;
        if (mod4 != 0)
        {
            trimmed = trimmed.PadRight(trimmed.Length + (4 - mod4), '=');
        }
        return trimmed;
    }
}
