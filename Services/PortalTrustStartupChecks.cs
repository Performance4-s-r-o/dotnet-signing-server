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
    /// <summary>A finding and whether it is an error (something stops working) or only a warning.</summary>
    public sealed record Finding(bool IsError, string Message);

    /// <summary>The findings for this configuration; empty = nothing to say.</summary>
    public static async Task<IReadOnlyList<Finding>> CheckAsync(
        PortalTrustOptions options, ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var flagged = await db.Users.AsNoTracking()
            .CountAsync(u => u.TrustedCaller && u.IsActive && u.ErasedAt == null, cancellationToken);
        var findings = new List<Finding>();
        if (options.InternalPort is int port && flagged == 0)
        {
            // An error, not a warning: tenant checks (403 TENANT_MISMATCH) are off and per-account
            // limits apply to the internal caller until an administrator sets the flag.
            findings.Add(new Finding(true,
                $"The internal port {port} has no trusted account: no caller is trusted until an administrator "
                + "marks one (Trusted caller) in the administration."));
        }
        else if (options.InternalPort == null && flagged > 0)
        {
            findings.Add(new Finding(false,
                $"{flagged} account(s) are marked as trusted caller but {PortalTrustOptions.InternalPortVariable} is empty: "
                + "the flag has no effect and their tokens are treated as ordinary ones."));
        }
        return findings;
    }

    public static async Task WarnAsync(
        PortalTrustOptions options, ApplicationDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        foreach (var finding in await CheckAsync(options, db, cancellationToken))
        {
            if (finding.IsError) logger.LogError("{Finding}", finding.Message);
            else logger.LogWarning("{Finding}", finding.Message);
        }
    }
}
