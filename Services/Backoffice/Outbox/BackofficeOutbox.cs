using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <inheritdoc />
public sealed class BackofficeOutbox : IBackofficeOutbox
{
    public const int MaxKindLength = 40;
    public const int MaxSubjectRefLength = 200;

    /// <summary>The service's JSON is snake_case; payloads are written the same way.</summary>
    public static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
    };

    private readonly ApplicationDbContext _db;
    private readonly OutboxPayloadProtector _protector;
    private readonly OutboxSignal _signal;
    private readonly TimeProvider _time;

    public BackofficeOutbox(
        ApplicationDbContext db,
        OutboxPayloadProtector protector,
        OutboxSignal signal,
        TimeProvider time)
    {
        _db = db;
        _protector = protector;
        _signal = signal;
        _time = time;
    }

    public Guid Enqueue(string kind, object payload, bool critical = false, string? subjectRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);
        if (kind.Length > MaxKindLength)
            throw new ArgumentException($"Outbox kind must have at most {MaxKindLength} characters.", nameof(kind));
        if (subjectRef?.Length > MaxSubjectRefLength)
            throw new ArgumentException($"Subject ref must have at most {MaxSubjectRefLength} characters.", nameof(subjectRef));

        var json = JsonSerializer.Serialize(payload, payload.GetType(), PayloadJson);
        var now = _time.GetUtcNow();
        var item = new BackofficeOutboxItem
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            PayloadProtected = _protector.Protect(json),
            Status = BackofficeOutboxStatus.Pending,
            Critical = critical,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            SubjectRef = subjectRef,
        };

        _db.BackofficeOutboxItems.Add(item);
        _db.OutboxPendingSignals.Signal ??= _signal;
        _db.OutboxPendingSignals.Add(item);
        return item.Id;
    }
}
