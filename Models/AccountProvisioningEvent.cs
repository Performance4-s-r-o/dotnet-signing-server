using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One call of the provisioning API (#131), written for every call: who asked, for which external
/// id, how it ended. No personal data and never a token, only the token's id.
/// </summary>
public class AccountProvisioningEvent
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The token that called; null when the caller had none (a cookie session).</summary>
    public Guid? TokenId { get; set; }

    /// <summary>The token's account: the parent for a provisioning token.</summary>
    public Guid? CallerUserId { get; set; }

    /// <summary>The provisioned account, when the call found or created one.</summary>
    public Guid? ChildUserId { get; set; }

    [MaxLength(User.MaxExternalIdLength)]
    public string? ExternalId { get; set; }

    /// <summary>What was asked: <c>create</c>, <c>list</c> or <c>scope</c> (refused before the endpoint).</summary>
    [MaxLength(16)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>One of <see cref="AccountProvisioningOutcomes"/>.</summary>
    [MaxLength(32)]
    public string Outcome { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ClientIp { get; set; }
}

public static class AccountProvisioningOutcomes
{
    public const string Created = "created";
    public const string Replayed = "replayed";
    public const string Conflict = "conflict";
    public const string Listed = "listed";
    public const string RefusedScope = "refused_scope";
    public const string RefusedLimit = "refused_limit";
    public const string RefusedParent = "refused_parent";
    public const string Invalid = "invalid";
}
