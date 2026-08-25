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

    public bool IsOriginAllowed(string origin, HttpContext context)
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

        var allowedOrigins = LoadCorsOrigins().Concat(LocalOrigins).Concat(
            [context.Request.Scheme + "://" + context.Request.Host.ToString()]
        ).ToHashSet();
        return allowedOrigins.Contains(normalized);
    }

    /// <summary>
    /// Origins reflected by the request-level CORS shim.
    ///
    /// When <c>Cors:AllowedOrigins</c> is configured, that operator-controlled list is
    /// authoritative. Without it we fall back to the union of every active browser
    /// token's origins across all users — which means any registered user can widen
    /// the server-wide CORS list just by creating a token, so production should
    /// always set the explicit list.
    /// </summary>
    private IEnumerable<string> LoadCorsOrigins()
    {
        var configured = LoadConfiguredCorsOrigins();
        return configured.Count > 0 ? configured : LoadAllowedOrigins();
    }

    private HashSet<string> LoadConfiguredCorsOrigins()
    {
        var section = _configuration.GetSection("Cors:AllowedOrigins");

        // Supports both an array (appsettings.json) and a delimited string
        // (Cors__AllowedOrigins env var).
        var raw = section.GetChildren().Any()
            ? string.Join(",", section.GetChildren().Select(c => c.Value))
            : section.Value;

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
