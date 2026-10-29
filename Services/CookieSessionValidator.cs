using System.Security.Claims;
using DotNetSigningServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Revalidation of the session cookie on every request (#80). The stamp alone was not enough:
/// whether a change of <c>IsActive</c> or <c>IsAdmin</c> reached an existing session depended on
/// the write path also moving <c>UpdatedAt</c>. Now:
/// <list type="bullet">
/// <item>no such user, inactive, no stamp or another stamp → the session is rejected;</item>
/// <item>the Admin role always follows the database, also when the flag was flipped in SQL
/// without touching the stamp.</item>
/// </list>
/// One indexed read per request, as before; there is no cache, so a change counts on the very
/// next request on every instance.
/// </summary>
public static class CookieSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var stamp = principal?.FindFirst(SessionClaims.SecurityStamp)?.Value;
        var userId = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (principal == null || stamp == null || !Guid.TryParse(userId, out var uid))
        {
            await RejectAsync(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == uid)
            .Select(u => new { u.UpdatedAt, u.IsActive, u.IsAdmin, u.IsServiceAccount })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        var decision = Decide(stamp, principal.IsInRole(SessionClaims.AdminRole), user?.UpdatedAt, user?.IsActive == true && user.IsServiceAccount == false, user?.IsAdmin);
        switch (decision)
        {
            case Decision.Reject:
                await RejectAsync(context);
                return;
            case Decision.RefreshRoles:
                context.ReplacePrincipal(WithRoles(principal, user!.IsAdmin));
                context.ShouldRenew = true;
                return;
        }
    }

    public enum Decision { Accept, Reject, RefreshRoles }

    /// <summary>The rule itself, pure.</summary>
    public static Decision Decide(string stamp, bool claimsAdmin, DateTimeOffset? updatedAt, bool? isActive, bool? isAdmin)
    {
        if (updatedAt is null || isActive != true) return Decision.Reject;
        // Compared at microsecond precision: PostgreSQL keeps microseconds, while the stamp in a
        // cookie issued straight after a save carries the in-memory value (100 ns ticks).
        if (!long.TryParse(stamp, out var stampTicks) || stampTicks / 10 != updatedAt.Value.Ticks / 10) return Decision.Reject;
        return claimsAdmin == (isAdmin == true) ? Decision.Accept : Decision.RefreshRoles;
    }

    private static ClaimsPrincipal WithRoles(ClaimsPrincipal principal, bool isAdmin)
    {
        var claims = principal.Claims.Where(c => c.Type != ClaimTypes.Role).ToList();
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, SessionClaims.AdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
