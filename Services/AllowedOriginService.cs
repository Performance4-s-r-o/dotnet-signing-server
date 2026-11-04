using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DotNetSigningServer.Services;

public class AllowedOriginService : IAllowedOriginService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    private static readonly string[] LocalOrigins =
    {
        "http://localhost",
        "https://localhost",
        "http://127.0.0.1",
        "https://127.0.0.1"
    };

    private static readonly string[] LocalHosts = { "localhost", "127.0.0.1", "::1" };

    public AllowedOriginService(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _configuration = configuration;
    }

    /// <summary>How long the set of browser-token origins is reused before the next database read.</summary>
    internal static readonly TimeSpan TokenOriginsTtl = TimeSpan.FromSeconds(30);

    private readonly object _tokenOriginsLock = new();
    private (long ExpiresAt, HashSet<string> Origins)? _tokenOrigins;

    /// <summary>
    /// The CORS shim on <c>/api</c> (#177): a preflight carries no token, so it passes an origin
    /// that at least one active browser token lists (or the server's own origin, or localhost in
    /// Development). Which token it is never shows; the real request then checks the origin of
    /// that very token (<see cref="IsOriginAllowedForToken"/>).
    ///
    /// <c>Cors:AllowedOrigins</c> is an optional operator restriction on top: when set, an origin
    /// must be in it as well. Empty does not switch CORS off, it leaves the decision to the tokens.
    /// </summary>
    public bool IsOriginAllowed(string origin, HttpContext context)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized == null)
        {
            return true; // non-browser requests
        }

        if (IsOwnOrigin(normalized, context))
        {
            return true;
        }

        if (_environment.IsDevelopment() && IsLocal(normalized))
        {
            return true;
        }

        var configured = LoadConfiguredCorsOrigins();
        if (configured.Count > 0 && !configured.Contains(normalized))
        {
            return false;
        }

        return TokenOrigins().Contains(normalized);
    }

    /// <summary>Whether <paramref name="origin"/> is the origin this request was addressed to.</summary>
    public bool IsOwnOrigin(string origin, HttpContext context)
    {
        var normalized = NormalizeOrigin(origin);
        var own = NormalizeOrigin(context.Request.Scheme + "://" + context.Request.Host);
        return normalized != null && own != null && string.Equals(normalized, own, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops the cached browser-token origins, so a new or revoked token counts at once in this process.</summary>
    public void InvalidateTokenOrigins()
    {
        lock (_tokenOriginsLock)
        {
            _tokenOrigins = null;
        }
    }

    private HashSet<string> TokenOrigins()
    {
        var now = Environment.TickCount64;
        var cached = _tokenOrigins;
        if (cached is { } hit && hit.ExpiresAt > now)
        {
            return hit.Origins;
        }

        lock (_tokenOriginsLock)
        {
            cached = _tokenOrigins;
            if (cached is { } again && again.ExpiresAt > now)
            {
                return again.Origins;
            }
            var origins = LoadAllowedOrigins();
            _tokenOrigins = (now + (long)TokenOriginsTtl.TotalMilliseconds, origins);
            return origins;
        }
    }

    private HashSet<string> LoadConfiguredCorsOrigins()
    {
        var section = _configuration.GetSection("Cors:AllowedOrigins");

        // Supports both an array (appsettings.json) and a delimited string
        // (Cors__AllowedOrigins env var).
        var raw = section.GetChildren().Any()
            ? string.Join(",", section.GetChildren().Select(c => c.Value))
            : section.Value;

        if (DotNetSigningServer.Options.EnvValues.IsUnset(raw))
        {
            return [];
        }
        return ParseOrigins(raw).ToHashSet();
    }

    public bool IsOriginAllowedForToken(string origin, ApiToken token)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized == null)
        {
            return false; // browser token must provide origin
        }

        // Localhost is a blanket pass only in development. In production it would
        // void the per-token origin lock entirely: the Origin header is trivially
        // set by any non-browser client, so a leaked browser token could be
        // replayed from anywhere with `Origin: http://localhost`.
        if (_environment.IsDevelopment() && IsLocal(normalized))
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

        return new HashSet<string>(origins.SelectMany(ParseOrigins), StringComparer.OrdinalIgnoreCase);
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
        // Exact host comparison. A StartsWith prefix match would accept
        // https://localhost.attacker.com and http://127.0.0.1.evil.com as "local".
        // DnsSafeHost (not Host) so IPv6 literals arrive as "::1", not "[::1]".
        return Uri.TryCreate(normalizedOrigin, UriKind.Absolute, out var uri)
               && LocalHosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsLocalOrigin(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        return normalized != null && IsLocal(normalized);
    }
}
