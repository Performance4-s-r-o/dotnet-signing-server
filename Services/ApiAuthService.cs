using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DotNetSigningServer.Services;

public class ApiAuthService : IApiAuthService
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(5);

    // Reverse index: tokenId -> cache keys, used to purge all entries for a token on revoke.
    private static readonly ConcurrentDictionary<Guid, ConcurrentBag<string>> _keysByTokenId = new();

    private readonly ApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly IAllowedOriginService _allowedOriginService;
    private readonly IIpWhitelistService _ipWhitelistService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApiAuthService> _logger;

    public ApiAuthService(
        ApplicationDbContext dbContext,
        ITokenService tokenService,
        IAllowedOriginService allowedOriginService,
        IIpWhitelistService ipWhitelistService,
        IMemoryCache cache,
        ILogger<ApiAuthService> logger)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _allowedOriginService = allowedOriginService;
        _ipWhitelistService = ipWhitelistService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<User?> ValidateTokenAsync(string authorizationHeader, string? originHeader = null, IPAddress? clientIp = null)
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

        var cacheKey = BuildCacheKey(token, originHeader, clientIp);
        if (_cache.TryGetValue<CachedValidation>(cacheKey, out var cached))
        {
            return cached?.User;
        }

        var result = await ValidateFromDatabaseAsync(token, originHeader, clientIp);

        _cache.Set(cacheKey, result, result?.User != null ? PositiveTtl : NegativeTtl);

        if (result?.TokenId is Guid id)
        {
            _keysByTokenId.GetOrAdd(id, _ => new ConcurrentBag<string>()).Add(cacheKey);
        }

        return result?.User;
    }

    public void InvalidateTokenCache(Guid tokenId)
    {
        if (_keysByTokenId.TryRemove(tokenId, out var keys))
        {
            foreach (var key in keys)
            {
                _cache.Remove(key);
            }
        }
    }

    private async Task<CachedValidation?> ValidateFromDatabaseAsync(string token, string? originHeader, IPAddress? clientIp)
    {
        var prefix = token.Length >= 8 ? token[..8] : token;
        var now = DateTimeOffset.UtcNow;

        // AsNoTracking — result is cached in a singleton IMemoryCache across requests/scopes.
        var candidates = await _dbContext.ApiTokens
            .AsNoTracking()
            .Include(t => t.User)
            .Where(t => t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Where(t => t.TokenPrefix == null || t.TokenPrefix == prefix)
            .ToListAsync();

        var apiToken = candidates.FirstOrDefault(t => _tokenService.VerifyToken(token, t.TokenHash));
        if (apiToken == null)
        {
            _logger.LogWarning("[auth] token rejected: no active token matched prefix={Prefix} candidates={Count}",
                prefix, candidates.Count);
            return null;
        }

        if (apiToken.IsBrowserToken)
        {
            var origin = originHeader?.Trim();
            if (string.IsNullOrWhiteSpace(origin))
            {
                _logger.LogWarning("[auth] token rejected: browser token without Origin (tokenId={TokenId})", apiToken.Id);
                return null;
            }

            if (!_allowedOriginService.IsOriginAllowedForToken(origin, apiToken))
            {
                _logger.LogWarning("[auth] token rejected: origin not allowed (tokenId={TokenId} origin={Origin})", apiToken.Id, origin);
                return null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(originHeader) && !_allowedOriginService.IsLocalOrigin(originHeader))
        {
            _logger.LogWarning("[auth] token rejected: non-browser token with non-local Origin (tokenId={TokenId} origin={Origin})", apiToken.Id, originHeader);
            return null;
        }

        if (!_ipWhitelistService.IsIpAllowedForToken(clientIp, apiToken))
        {
            _logger.LogWarning("[auth] token rejected: IP not whitelisted (tokenId={TokenId} ip={Ip})", apiToken.Id, clientIp);
            return null;
        }

        if (apiToken.User != null && !apiToken.User.IsActive)
        {
            _logger.LogWarning("[auth] token rejected: user inactive (tokenId={TokenId} userId={UserId})", apiToken.Id, apiToken.User.Id);
            return null;
        }

        return new CachedValidation { User = apiToken.User, TokenId = apiToken.Id };
    }

    private static string BuildCacheKey(string token, string? originHeader, IPAddress? clientIp)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var origin = originHeader?.Trim().ToLowerInvariant() ?? string.Empty;
        var ip = clientIp?.ToString() ?? string.Empty;
        return $"apiauth:{tokenHash}:{origin}:{ip}";
    }

    private static string NormalizeToken(string token)
    {
        var trimmed = token.Trim().Trim('"');
        trimmed = trimmed.Replace(' ', '+');
        int mod4 = trimmed.Length % 4;
        if (mod4 != 0)
        {
            trimmed = trimmed.PadRight(trimmed.Length + (4 - mod4), '=');
        }
        return trimmed;
    }

    private sealed class CachedValidation
    {
        public User? User { get; init; }
        public Guid? TokenId { get; init; }
    }
}
