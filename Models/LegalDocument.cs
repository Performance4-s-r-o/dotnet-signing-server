using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// Snapshot of a legal document (Terms, Privacy, DPA, …) as the P4 Backoffice
/// service last served it. The texts are written in the service, never here;
/// a row is served only once <see cref="EffectiveFrom"/> has passed, <see cref="IsDraft"/>
/// is false and <see cref="ContentHtml"/> is set.
///
/// When no effective row exists for a (Slug, Locale), LegalController falls back
/// to the English row and finally to a "not available" page.
/// </summary>
public class LegalDocument
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Document type. Must match the slug LegalController passes for the route,
    /// e.g. terms-of-service, privacy-policy, data-processing-agreement,
    /// service-level-agreement, refund-policy, cookies-policy.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Two-letter locale — "en" or "cs".</summary>
    [Required]
    [MaxLength(8)]
    public string Locale { get; set; } = "en";

    /// <summary>
    /// Version number, shown to the reader. Unique per (Slug, Locale) — bump it
    /// for every new row rather than editing a published one, so the previous
    /// wording stays on record.
    /// </summary>
    public int Version { get; set; } = 1;

    [Required]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional "key changes" note rendered above the content.</summary>
    [MaxLength(1024)]
    public string? Summary { get; set; }

    /// <summary>
    /// Unused: the text is <see cref="ContentHtml"/>. Kept empty so no migration is needed.
    /// </summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Publication date. The row is invisible to visitors until this moment,
    /// after which it supersedes older rows for the same (Slug, Locale) —
    /// unless <see cref="IsDraft"/> is still set.
    /// </summary>
    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// While true the row is never served, no matter what
    /// <see cref="EffectiveFrom"/> says. Lets you record the real publication
    /// date up front and keep working on the text; clearing the flag arms the
    /// row, and it goes live at that date (or immediately, if it has passed).
    ///
    /// Defaults to false, so an ordinary insert publishes per its date — set it
    /// explicitly to park a work in progress.
    /// </summary>
    public bool IsDraft { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Snapshot of the P4 Backoffice service (Modules:Docs) ────────────────
    // The last successful answer of the service, served while it is unreachable.

    /// <summary>Service document type (<c>terms</c>, <c>privacy</c>, …); see <c>LegalSlugMap</c>.</summary>
    [MaxLength(40)]
    public string? TypeKey { get; set; }

    /// <summary>Sanitized HTML as served by the service; rendered as is.</summary>
    public string? ContentHtml { get; set; }

    /// <summary>SHA-256 (hex) of <see cref="ContentHtml"/> as reported by the service.</summary>
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary><c>material</c>, <c>notice</c> or <c>minor</c>.</summary>
    [MaxLength(16)]
    public string? ChangeKind { get; set; }

    /// <summary>
    /// <see cref="LegalDocumentSources.Backoffice"/> for rows created from the service
    /// (<see cref="LegalDocumentSources.Manual"/> is only the column default of rows from before).
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string Source { get; set; } = LegalDocumentSources.Manual;

    /// <summary>When the service text in this row was last fetched.</summary>
    public DateTimeOffset? FetchedAt { get; set; }
}

/// <summary>Known <see cref="LegalDocument.Source"/> values.</summary>
public static class LegalDocumentSources
{
    public const string Manual = "manual";
    public const string Backoffice = "backoffice";
}
