using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Start-up warnings about the trusted caller (#78). Never fatal: the flag is set in the
/// administration, which needs a running server, and a rebuilt database has no flagged account yet.
/// </summary>
public static class PortalTrustStartupChecks
{
    /// <summary>The warnings for this configuration; empty = nothing to say.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        PortalTrustOptions options, ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var flagged = await db.Users.AsNoTracking()
            .CountAsync(u => u.TrustedCaller && u.IsActive && u.ErasedAt == null, cancellationToken);
        var warnings = new List<string>();
        if (options.InternalPort is int port && flagged == 0)
        {
            warnings.Add(
                $"The internal port {port} has no trusted account: no caller is trusted until an administrator "
                + "marks one (Trusted caller) in the administration.");
        }
        else if (options.InternalPort == null && flagged > 0)
        {
            warnings.Add(
                $"{flagged} account(s) are marked as trusted caller but {PortalTrustOptions.InternalPortVariable} is empty: "
                + "the flag has no effect and their tokens are treated as ordinary ones.");
        }
        return warnings;
    }

    public static async Task WarnAsync(
        PortalTrustOptions options, ApplicationDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        foreach (var warning in await CheckAsync(options, db, cancellationToken))
        {
            logger.LogWarning("{Warning}", warning);
        }
    }
}
