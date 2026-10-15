using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One administrative action (#80): who did it, to whom, what changed. Append-only — the
/// context refuses to update or delete a row and a database trigger refuses it for anybody
/// else, so the trail cannot be tidied up by the people it records.
/// </summary>
public class AdminAuditEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ActorUserId { get; set; }

    /// <summary>The actor's address at the time: the account may be renamed or deleted later.</summary>
    [MaxLength(256)]
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>The account acted upon; NULL for actions on the installation itself.</summary>
    public Guid? TargetUserId { get; set; }

    /// <summary>Machine name of the action, e.g. <c>user.seal_allowed</c>.</summary>
    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    /// <summary>JSON: the values before and after (or the counts of a bulk action).</summary>
    public string Change { get; set; } = "{}";
}
