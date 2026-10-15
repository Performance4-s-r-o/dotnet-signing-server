using System.Security.Claims;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

/// <summary>
/// Records an admin action (#80) in the same <c>SaveChangesAsync</c> as the change itself: the
/// action and its trail commit together or not at all.
/// </summary>
public static class AdminAudit
{
    public static class Actions
    {
        public const string Enterprise = "user.enterprise";
        public const string SealAllowed = "user.seal_allowed";
        public const string ConcurrencyQueueTimeout = "user.concurrency_queue_timeout";
        public const string OutboxRequeue = "backoffice.outbox_requeue";
        public const string ConsentsBackfill = "backoffice.consents_backfill";
    }

    public static AdminAuditEntry Record(ApplicationDbContext db, ClaimsPrincipal actor, Guid? targetUserId, string action, object change, DateTimeOffset? at = null)
    {
        var entry = new AdminAuditEntry
        {
            CreatedAt = at ?? DateTimeOffset.UtcNow,
            ActorUserId = Guid.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty,
            ActorEmail = Truncate(actor.FindFirstValue(ClaimTypes.Name) ?? string.Empty, 256),
            TargetUserId = targetUserId,
            Action = action,
            Change = JsonSerializer.Serialize(change),
        };
        db.AdminAuditEntries.Add(entry);
        return entry;
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
