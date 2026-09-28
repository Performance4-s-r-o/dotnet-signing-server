using DotNetSigningServer.Models;
using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services.Consents;

/// <summary>A document a user has to consent to (<c>granted</c>) or be informed about (<c>acknowledged</c>).</summary>
/// <param name="Document">Service document type (<c>terms</c>, <c>dpa</c>, <c>privacy</c>).</param>
/// <param name="Action"><see cref="ConsentActions.Granted"/> or <see cref="ConsentActions.Acknowledged"/>.</param>
public sealed record ConsentRequirement(string Document, string Action)
{
    public bool IsGrant => Action == ConsentActions.Granted;
}

/// <summary>
/// What is recorded at sign-up and what the re-consent gate checks, from
/// <c>P4Backoffice:Consents:Documents</c> (granted, one checkbox) and
/// <c>P4Backoffice:Consents:Acknowledged</c> (an information sentence). Pure.
/// </summary>
public static class ConsentRequirements
{
    /// <summary>Prefix of the pseudonymous subject sent to the service.</summary>
    public const string SubjectPrefix = "dotnet:user:";

    /// <summary>Outbox kind of a consent batch.</summary>
    public const string OutboxKind = "consent";

    /// <summary>The service accepts at most this many events per <c>POST /v1/consents</c>.</summary>
    public const int MaxEventsPerBatch = 100;

    /// <summary>Documents whose events carry the end user's IP (the service stores it truncated).</summary>
    public static readonly IReadOnlySet<string> DocumentsWithIp = new HashSet<string>(StringComparer.Ordinal) { "dpa" };

    public static IReadOnlyList<ConsentRequirement> From(P4BackofficeProductOptions.ConsentsOptions options) =>
        options.EffectiveDocuments.Select(d => new ConsentRequirement(d, ConsentActions.Granted))
            .Concat(options.EffectiveAcknowledged.Select(d => new ConsentRequirement(d, ConsentActions.Acknowledged)))
            .DistinctBy(r => r.Document)
            .ToList();

    /// <summary><c>dotnet:user:{id}</c> — never an e-mail address.</summary>
    public static string SubjectRef(Guid userId) => SubjectPrefix + userId.ToString("D");
}
