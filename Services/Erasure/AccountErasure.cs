using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Services.Erasure;

/// <summary>
/// Names and shapes of the account erasure (GDPR art. 17, #217) and of its contract with
/// p4-backoffice (<c>docs/erasure-contract.md</c> there: <c>POST /v1/erasures</c>, the
/// <c>subject.erasure.requested</c> webhook and its <c>ack</c>). Pure.
/// </summary>
public static class AccountErasure
{
    /// <summary>Outbox kind of <c>POST /v1/erasures</c> (the same name the portal uses).</summary>
    public const string RequestOutboxKind = "subject.erase";

    /// <summary>Outbox kind of <c>POST /v1/erasures/{id}/targets/{product}/ack</c>.</summary>
    public const string AckOutboxKind = "subject.erasure.ack";

    /// <summary>Kinds of the erasure itself, which the erasure never cancels.</summary>
    public const string KindPrefix = "subject.";

    /// <summary>
    /// Both kinds are retried this long (the service recognises a repeat by the subject, not
    /// only by the <c>Idempotency-Key</c>) and never end quietly: after it the item is Dead,
    /// logged as an error, and kept until somebody settles it.
    /// </summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(30);

    /// <summary>From this age on, every failed attempt of an erasure item is an alert.</summary>
    public static readonly TimeSpan AlertAfter = TimeSpan.FromHours(72);

    /// <summary>Values of <c>origin</c> this product sends or receives.</summary>
    public static class Origins
    {
        public const string AccountDeleted = "account_deleted";
        public const string SubjectRequest = "subject_request";
    }

    /// <summary>Values of the ack's <c>status</c>.</summary>
    public static class AckStatuses
    {
        public const string Done = "done";
        public const string Retained = "retained";
        public const string Failed = "failed";
    }

    /// <summary>Values of <c>retained_reason</c> used here (closed list of the contract).</summary>
    public static class RetainedReasons
    {
        /// <summary>Invoices and payments: Act on accounting § 31–32, 10 years.</summary>
        public const string Accounting = "accounting_10y";

        /// <summary>The account's own administrator actions in the audit trail (art. 17(3)(e)).</summary>
        public const string LegalClaims = "legal_claims";

        /// <summary>Pseudonymised consent records: proof of consent, art. 7(1).</summary>
        public const string ConsentEvidence = "consent_evidence";
    }

    /// <summary>Prefix of the product's own target in an erasure request (<c>product:{slug}</c>).</summary>
    public const string ProductTargetPrefix = "product:";

    /// <summary>Domain of every tombstone address; <c>.invalid</c> is reserved (RFC 2606) and never delivers.</summary>
    public const string TombstoneDomain = "@invalid";

    /// <summary>
    /// The address an erased account keeps: <c>erased+{hash}@invalid</c>, where the hash comes
    /// from the user id — never from the old address — so it is unique (the e-mail index) and
    /// says nothing about the person.
    /// </summary>
    public static string TombstoneEmail(Guid userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dotnet-signing-server/erased-user/v1:" + userId.ToString("D")));
        return "erased+" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + TombstoneDomain;
    }

    /// <summary>The subject this product sends to the service: <c>dotnet:user:{id}</c>.</summary>
    public static string SubjectRef(Guid userId) => ConsentRequirements.SubjectRef(userId);

    /// <summary>The user id of a ref this product sent; false for any other product's ref.</summary>
    public static bool TryParseSubjectRef(string? subjectRef, out Guid userId)
    {
        userId = Guid.Empty;
        return subjectRef is not null
               && subjectRef.StartsWith(ConsentRequirements.SubjectPrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(subjectRef[ConsentRequirements.SubjectPrefix.Length..], "D", out userId);
    }

    /// <summary>A product slug as it may appear in the ack's path.</summary>
    public static bool IsValidProductSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= 64
        && slug.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}

/// <summary>Body of <c>POST /v1/erasures</c> (<c>ErasureCreate</c>), stored encrypted in the outbox.</summary>
public sealed class ErasureRequestPayload
{
    public string SubjectRef { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Emails { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomerRef { get; init; }

    public string Origin { get; init; } = AccountErasure.Origins.AccountDeleted;
}

/// <summary>
/// An ack waiting in the outbox: where it goes (<see cref="ErasureId"/>, <see cref="Product"/>)
/// and the body (<c>ErasureAck</c>: status, counts, retained_reason). Counts carry no personal data.
/// </summary>
public sealed class ErasureAckPayload
{
    public Guid ErasureId { get; init; }

    public string Product { get; init; } = "";

    public string Status { get; init; } = AccountErasure.AckStatuses.Done;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetainedReason { get; init; }
}
