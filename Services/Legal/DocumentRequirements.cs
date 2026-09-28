using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetSigningServer.Services.Backoffice.Documents;

namespace DotNetSigningServer.Services.Legal;

/// <summary>Consent-relevant facts about one document, stored in <c>docs:meta</c>.</summary>
/// <param name="CurrentVersion">Version in force now.</param>
/// <param name="RequiredVersion">Oldest version a consent may name and still count (see <see cref="DocumentRequirements.RequiredVersion"/>).</param>
/// <param name="Upcoming">Approved version that is not in force yet (for a notice banner).</param>
/// <param name="CurrentSince">
/// When <paramref name="CurrentVersion"/> came into force (the later of its effective date and
/// publication); the sign-up form accepts the previous version for a short while after it.
/// Null in entries written before this field existed.
/// </param>
/// <param name="CurrentSummary">"What changed" note of the version in force.</param>
public sealed record DocumentMeta(
    [property: JsonPropertyName("requires_consent")] bool RequiresConsent,
    [property: JsonPropertyName("current_version")] int? CurrentVersion,
    [property: JsonPropertyName("required_version")] int? RequiredVersion,
    [property: JsonPropertyName("upcoming")] DocumentUpcomingMeta? Upcoming,
    [property: JsonPropertyName("current_since")] DateTimeOffset? CurrentSince = null,
    [property: JsonPropertyName("current_summary")] string? CurrentSummary = null);

public sealed record DocumentUpcomingMeta(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("change_kind")] string ChangeKind,
    [property: JsonPropertyName("effective_from")] DateTimeOffset? EffectiveFrom,
    [property: JsonPropertyName("summary")] string? Summary);

/// <summary>Value of the <c>docs:meta</c> state row: every document of the product by type.</summary>
public sealed record DocumentsMeta(
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("documents")] Dictionary<string, DocumentMeta> Documents)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a stored value; null when it is missing or unreadable.</summary>
    public static DocumentsMeta? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<DocumentsMeta>(json, Json);
            return meta?.Documents is null ? null : meta;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Which document version a consent has to name. Pure.</summary>
public static class DocumentRequirements
{
    public const string Material = "material";

    /// <summary>Statuses of versions that were approved and published (drafts never count).</summary>
    private static readonly HashSet<string> Published = new(StringComparer.OrdinalIgnoreCase)
    {
        "effective", "superseded", "scheduled",
    };

    /// <summary>
    /// The newest <c>material</c> version already in force at <paramref name="now"/>: a consent
    /// to that version or a later one is current; <c>notice</c> and <c>minor</c> changes do not
    /// ask users again. When no published version is material, the first version in force is
    /// required (a first version always needs consent, whatever its change kind says).
    /// Null when nothing is in force yet.
    /// </summary>
    public static int? RequiredVersion(IEnumerable<BackofficeDocumentVersion> versions, DateTimeOffset now)
    {
        var inForce = versions
            .Where(v => Published.Contains(v.Status) && v.EffectiveFrom is { } from && from <= now)
            .ToList();
        if (inForce.Count == 0) return null;

        var material = inForce.Where(v => string.Equals(v.ChangeKind, Material, StringComparison.OrdinalIgnoreCase)).ToList();
        return material.Count > 0 ? material.Max(v => v.Version) : inForce.Min(v => v.Version);
    }

    /// <summary>The version in force at <paramref name="now"/> (the newest published one whose date has passed).</summary>
    public static int? CurrentVersion(IEnumerable<BackofficeDocumentVersion> versions, DateTimeOffset now) =>
        versions
            .Where(v => Published.Contains(v.Status) && v.EffectiveFrom is { } from && from <= now)
            .Select(v => (int?)v.Version)
            .Max();

    /// <summary>Builds the <c>docs:meta</c> entry of one document.</summary>
    public static DocumentMeta Meta(BackofficeDocumentSummary summary, IEnumerable<BackofficeDocumentVersion> versions, DateTimeOffset now)
    {
        var list = versions.ToList();
        var upcoming = summary.Upcoming is { } u
            ? new DocumentUpcomingMeta(u.Version, u.ChangeKind, u.EffectiveFrom, u.Summary)
            : null;
        var currentVersion = summary.Current?.Version ?? CurrentVersion(list, now);
        var current = list.FirstOrDefault(v => v.Version == currentVersion)
                      ?? (summary.Current?.Version == currentVersion ? summary.Current : null);
        return new DocumentMeta(
            summary.RequiresConsent,
            currentVersion,
            RequiredVersion(list, now),
            upcoming,
            current is null ? null : InForceSince(current),
            current?.Summary);
    }

    /// <summary>When a version came into force: the later of its effective date and its publication.</summary>
    public static DateTimeOffset? InForceSince(BackofficeDocumentVersion version) =>
        (version.EffectiveFrom, version.PublishedAt) switch
        {
            ({ } from, { } published) => from > published ? from : published,
            ({ } from, null) => from,
            (null, { } published) => published,
            _ => null,
        };
}
