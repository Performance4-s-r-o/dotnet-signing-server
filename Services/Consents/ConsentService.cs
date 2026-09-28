using System.Text.Json.Serialization;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Consents;

/// <summary>A decision to record: the document as the form showed it and the version accepted.</summary>
/// <param name="Version">Usually <see cref="ConsentDocumentVersion.Version"/>; the previous version within the grace period.</param>
/// <param name="ContentHash">Hash of that version's text; null when not known locally.</param>
public sealed record ConsentChoice(ConsentDocumentVersion Document, int Version, string? ContentHash);

/// <summary>Body of <c>POST /v1/consents</c> (<c>ConsentBatch</c>).</summary>
public sealed record ConsentBatchPayload(IReadOnlyList<ConsentEventPayload> Events);

/// <summary>One <c>ConsentInput</c> of the service (snake_case on the wire).</summary>
public sealed record ConsentEventPayload(
    string SubjectType,
    string SubjectRef,
    string Document,
    int Version,
    string Locale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash,
    string Purpose,
    string Action,
    DateTimeOffset OccurredAt,
    string Channel,
    string? UserAgent,
    string? Ip,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>
/// Records consent decisions: one <see cref="ConsentRecord"/> per document and — with the
/// Consents module Shadow or On — one outbox item <c>consent</c> carrying all of them as a
/// single batch. Only adds to the scoped <see cref="ApplicationDbContext"/>; the caller's own
/// <c>SaveChangesAsync</c> writes the records, the outbox item and the domain change (the new
/// user) in one transaction. Never calls the service.
/// </summary>
public sealed class ConsentService
{
    public const int MaxUserAgentLength = 512;

    /// <summary><c>metadata.flow</c> values.</summary>
    public static class Flows
    {
        public const string Signup = "signup";
        public const string Reconsent = "reconsent";

        /// <summary>A user registered before consents were recorded, confirming for the first time.</summary>
        public const string Initial = "initial";
    }

    private readonly ApplicationDbContext _db;
    private readonly IBackofficeOutbox _outbox;
    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;

    public ConsentService(
        ApplicationDbContext db,
        IBackofficeOutbox outbox,
        IOptions<P4BackofficeProductOptions> options,
        TimeProvider time)
    {
        _db = db;
        _outbox = outbox;
        _options = options;
        _time = time;
    }

    /// <summary>Consents given on the sign-up form (<c>source = signup</c>).</summary>
    public IReadOnlyList<ConsentRecord> RecordSignupConsents(
        User user, IReadOnlyList<ConsentChoice> shown, string? userAgent, string? ip = null) =>
        Record(user.Id, shown, ConsentSources.Signup, Flows.Signup, userAgent, ip);

    /// <summary>Consents given on <c>/Account/Consent</c> (<c>source = reconsent</c>).</summary>
    public IReadOnlyList<ConsentRecord> RecordReconsent(
        Guid userId, IReadOnlyList<ConsentChoice> shown, string flow, string? userAgent, string? ip = null) =>
        Record(userId, shown, ConsentSources.Reconsent, flow, userAgent, ip);

    private IReadOnlyList<ConsentRecord> Record(
        Guid userId, IReadOnlyList<ConsentChoice> shown, string source, string flow, string? userAgent, string? ip)
    {
        if (shown.Count == 0) return [];
        if (shown.Count > ConsentRequirements.MaxEventsPerBatch)
            throw new ArgumentException($"At most {ConsentRequirements.MaxEventsPerBatch} documents per decision.", nameof(shown));

        var now = _time.GetUtcNow();
        var subjectRef = ConsentRequirements.SubjectRef(userId);
        var agent = Truncate(userAgent, MaxUserAgentLength);
        var records = shown.Select(choice => new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubjectRef = subjectRef,
            Document = choice.Document.Document,
            Purpose = choice.Document.Document,
            Version = choice.Version,
            Locale = choice.Document.Locale,
            ContentHash = choice.ContentHash,
            Action = choice.Document.Action,
            Source = source,
            Channel = "web",
            OccurredAt = now,
            UserAgent = agent,
        }).ToList();

        // Off: the local record only; the backfill sends it once the module is switched on.
        if (_options.Value.ModeFor(BackofficeModule.Consents) != BackofficeMode.Off)
        {
            var outboxId = _outbox.Enqueue(
                ConsentRequirements.OutboxKind, Batch(records, ip, flow), critical: false, subjectRef: subjectRef);
            foreach (var record in records) record.OutboxItemId = outboxId;
        }

        _db.ConsentRecords.AddRange(records);
        return records;
    }

    /// <summary>The <c>POST /v1/consents</c> body for <paramref name="records"/>.</summary>
    /// <param name="ip">End user's IP; sent only for documents with legal weight (DPA), else null.</param>
    public static ConsentBatchPayload Batch(IEnumerable<ConsentRecord> records, string? ip, string flow, bool backfill = false) =>
        new(records.Select(r => new ConsentEventPayload(
            SubjectType: "user",
            SubjectRef: r.SubjectRef,
            Document: r.Document,
            Version: r.Version,
            Locale: r.Locale,
            ContentHash: r.ContentHash,
            Purpose: r.Purpose,
            Action: r.Action,
            OccurredAt: r.OccurredAt.ToUniversalTime(),
            Channel: r.Channel,
            UserAgent: r.UserAgent,
            Ip: ConsentRequirements.DocumentsWithIp.Contains(r.Document) && !string.IsNullOrWhiteSpace(ip) ? ip : null,
            Metadata: backfill
                ? new Dictionary<string, object?> { ["flow"] = flow, ["backfill"] = true }
                : new Dictionary<string, object?> { ["flow"] = flow })).ToList());

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
