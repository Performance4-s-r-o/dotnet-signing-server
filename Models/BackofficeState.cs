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

    /// <summary>
    /// Consent-relevant facts of every document (<c>requires_consent</c>, <c>current_version</c>,
    /// <c>required_version</c>, <c>upcoming</c>) as JSON; see <c>DocumentsMeta</c>.
    /// </summary>
    public const string DocsMeta = "docs:meta";

    /// <summary>Consent records up to this time (ISO 8601) were queued by the consent backfill.</summary>
    public const string ConsentsBackfilledUntil = "consents:backfilled_until";

    /// <summary>When the daily consent reconciliation last ran (ISO 8601).</summary>
    public const string ConsentsReconciledAt = "consents:reconciled_at";

    /// <summary>
    /// The price list in force (<c>GET /v1/pricing/current</c>) with its ETag, as JSON; see
    /// <c>PricingSnapshot</c>.
    /// </summary>
    public const string PricingCurrent = "pricing:current";

    // Per-language keys: "cookies:{locale}" (cookie declaration, CookieDeclarationReader) and
    // "support:categories:{locale}" (SupportCategoriesWorker).
}
