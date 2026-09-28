using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// Small key/value state of the P4 Backoffice integration that must survive a restart:
/// the <c>/v1/events</c> cursor (<see cref="BackofficeStateKeys.EventsCursor"/>) and, later,
/// snapshots such as the current price list.
/// </summary>
public class BackofficeState
{
    [Key]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>Plain text or JSON, depending on the key.</summary>
    [Required]
    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Known <see cref="BackofficeState.Key"/> values.</summary>
public static class BackofficeStateKeys
{
    /// <summary><c>next_cursor</c> of the last page read from <c>GET /v1/events</c>.</summary>
    public const string EventsCursor = "events:cursor";
}
