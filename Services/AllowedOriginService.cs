using System.Security.Policy;
using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DotNetSigningServer.Services;

public class AllowedOriginService : IAllowedOriginService
{
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly string[] LocalOrigins =
    {
        "http://localhost",
        "https://localhost",
        "http://127.0.0.1",
        "https://127.0.0.1",
        "https://signproxy.cfy.performance4.cz"
    };

    public AllowedOriginService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public bool IsOriginAllowed(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized == null)
        {
            return true; // non-browser requests
        }

        if (IsLocal(normalized))
        {
            return true;
        }

        var allowedOrigins = LoadAllowedOrigins().Concat(LocalOrigins).ToHashSet();
        return allowedOrigins.Contains(normalized);
    }

    public bool IsOriginAllowedForToken(string origin, ApiToken token)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized == null)
        {
            return false; // browser token must provide origin
        }

        if (IsLocal(normalized))
        {
            return true;
        }

        var allowed = ParseOrigins(token.AllowedOrigins);
        return allowed.Contains(normalized);
    }

    private HashSet<string> LoadAllowedOrigins()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow;

        var origins = db.ApiTokens
            .Where(t => t.IsBrowserToken && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Select(t => t.AllowedOrigins)
            .ToList();

        return new HashSet<string>(origins.SelectMany(ParseOrigins));
    }

    private static IEnumerable<string> ParseOrigins(string? rawOrigins)
    {
        if (string.IsNullOrWhiteSpace(rawOrigins))
        {
            return Enumerable.Empty<string>();
        }

        var split = Regex.Split(rawOrigins, @"[\s,;]+", RegexOptions.Compiled);
        return split
            .Select(NormalizeOrigin)
            .Where(o => o != null && (o.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || IsLocal(o!)))
            .Select(o => o!);
    }

    private static string? NormalizeOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsLocal(string normalizedOrigin)
    {
        return LocalOrigins.Any(local => normalizedOrigin.StartsWith(local, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsLocalOrigin(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        return normalized != null && IsLocal(normalized);
    }
}
