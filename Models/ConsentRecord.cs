using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// One consent decision of a user about one legal document (append-only).
///
/// The local copy is the operational source for the re-consent gate; the P4 Backoffice
/// service keeps the evidence (<c>POST /v1/consents</c> through the outbox). Rows are never
/// updated or deleted: on PostgreSQL a trigger refuses <c>UPDATE</c> and <c>DELETE</c>, and a
/// new decision (re-consent, revocation) is a new row.
/// </summary>
public class ConsentRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user; deliberately no foreign key, the record outlives the account.</summary>
    public Guid UserId { get; set; }

    /// <summary>Pseudonymous subject sent to the service, <c>dotnet:user:{UserId}</c> (never an e-mail).</summary>
    [Required]
    [MaxLength(200)]
    public string SubjectRef { get; set; } = string.Empty;

    /// <summary>Service document type (<c>terms</c>, <c>dpa</c>, <c>privacy</c>, …).</summary>
    [Required]
    [MaxLength(40)]
    public string Document { get; set; } = string.Empty;

    /// <summary>Purpose; the document type unless one document covers several purposes.</summary>
    [Required]
    [MaxLength(64)]
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Version of the text the user was shown.</summary>
    public int Version { get; set; }

    /// <summary>Language of the text the user was shown (<c>en</c>, <c>cs</c>).</summary>
    [Required]
    [MaxLength(8)]
    public string Locale { get; set; } = "en";

    /// <summary>SHA-256 (hex) of the text shown, when known (the service's <c>content_hash</c>).</summary>
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary>One of <see cref="ConsentActions"/>.</summary>
    [Required]
    [MaxLength(16)]
    public string Action { get; set; } = ConsentActions.Granted;

    /// <summary>One of <see cref="ConsentSources"/>.</summary>
    [Required]
    [MaxLength(16)]
    public string Source { get; set; } = ConsentSources.Signup;

    /// <summary>Always <c>web</c> in this product.</summary>
    [Required]
    [MaxLength(16)]
    public string Channel { get; set; } = "web";

    public DateTimeOffset OccurredAt { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    /// <summary>Outbox item that sends this record to the service; null when the Consents module was Off.</summary>
    public Guid? OutboxItemId { get; set; }
}

/// <summary>Values of <see cref="ConsentRecord.Action"/> (same as the service's <c>action</c>).</summary>
public static class ConsentActions
{
    public const string Granted = "granted";
    public const string Revoked = "revoked";
    public const string Acknowledged = "acknowledged";
}

/// <summary>Values of <see cref="ConsentRecord.Source"/>.</summary>
public static class ConsentSources
{
    /// <summary>Checkbox on the sign-up form.</summary>
    public const string Signup = "signup";

    /// <summary>The <c>/Account/Consent</c> page (new version, or a user without records).</summary>
    public const string Reconsent = "reconsent";
}
