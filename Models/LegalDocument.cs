using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// A versioned legal/policy document (Terms, Privacy, DPA, …) owned by this
/// platform. Rows are maintained by hand — there is no admin UI — so the shape
/// is deliberately small.
///
/// A document is served only once <see cref="EffectiveFrom"/> has passed and
/// <see cref="IsDraft"/> is false. That is what makes writing the next version
/// ahead of time safe: insert the row with its real future date, leave it a
/// draft while you work on the wording, and clear the flag when it is ready —
/// it then takes over on its own at the date already agreed.
///
/// When no effective row exists for a (Slug, Locale), LegalController falls back
/// to the English row and finally to the static Razor view in Views/Legal/.
/// </summary>
public class LegalDocument
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Document type. Must match the slug LegalController passes for the route,
    /// e.g. terms-of-service, privacy-policy, data-processing-agreement,
    /// service-level-agreement, refund-policy, cookies-policy,
    /// open-source-notices, license.
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
    /// Document body in Markdown. Rendered with Markdig with raw HTML disabled,
    /// so any HTML written here is escaped, not executed.
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
}
