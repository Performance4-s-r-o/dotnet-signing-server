using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DotNetSigningServer.Services;

/// <summary>Claims of the web session cookie and of an API token principal (#80).</summary>
public static class SessionClaims
{
    /// <summary><c>User.UpdatedAt</c> ticks at sign-in; a different value in the database voids the cookie.</summary>
    public const string SecurityStamp = "SecurityStamp";

    /// <summary>Unix seconds of the last password or 2FA confirmation (sign-in or re-auth).</summary>
    public const string AuthTime = "auth_time";

    /// <summary>API token principal: the token's id.</summary>
    public const string TokenId = "p4:token_id";

    /// <summary>API token principal: <see cref="BrowserTokenKind"/> or <see cref="ServerTokenKind"/>.</summary>
    public const string TokenKind = "p4:token_kind";

    public const string BrowserTokenKind = "browser";
    public const string ServerTokenKind = "server";

    public const string AdminRole = "Admin";

    public static ClaimsPrincipal CookiePrincipal(Models.User user, DateTimeOffset authTime)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
            new(SecurityStamp, user.UpdatedAt.Ticks.ToString()),
            new(AuthTime, authTime.ToUnixTimeSeconds().ToString()),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static DateTimeOffset? AuthTimeOf(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.FindFirst(AuthTime)?.Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    public static bool IsBrowserToken(ClaimsPrincipal? principal) =>
        principal?.FindFirst(TokenKind)?.Value == BrowserTokenKind;

    public static Guid? TokenIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(TokenId)?.Value, out var id) ? id : null;
}
