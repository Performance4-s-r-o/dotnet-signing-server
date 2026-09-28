using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Consents;

/// <summary>Outcome of <see cref="ConsentBackfill.RunAsync"/>.</summary>
public sealed record ConsentBackfillResult(int Records, int Batches);

/// <summary>
/// One-off catch-up after the Consents module was Off: queues every <see cref="ConsentRecord"/>
/// that has no outbox item (<c>OutboxItemId</c> null) and is newer than the last backfill, as
/// batches of the same decision (user, time, source). The records stay untouched (append-only);
/// the <c>consents:backfilled_until</c> state row remembers how far it got, written in the same
/// <c>SaveChangesAsync</c> as the outbox items, so running it twice sends nothing twice.
/// On PostgreSQL the run holds a transaction advisory lock against a parallel run.
/// </summary>
public sealed class ConsentBackfill
{
    /// <summary>"P4BOCBFL" as ASCII.</summary>
    public const long AdvisoryLockKey = 0x5034_424F_4342_464C;

    private readonly ApplicationDbContext _db;
    private readonly IBackofficeOutbox _outbox;
    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;

    public ConsentBackfill(ApplicationDbContext db, IBackofficeOutbox outbox, IOptions<P4BackofficeProductOptions> options, TimeProvider time)
    {
        _db = db;
        _outbox = outbox;
        _options = options;
        _time = time;
    }

    public async Task<ConsentBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Value.ModeFor(BackofficeModule.Consents) == BackofficeMode.Off)
        {
            throw new InvalidOperationException("The Consents module is Off; switch it to Shadow or On before the backfill.");
        }

        if (!_db.Database.IsNpgsql())
        {
            return await RunOnceAsync(cancellationToken);
        }

        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            var result = await RunOnceAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    private async Task<ConsentBackfillResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow();
        var from = DateTimeOffset.TryParse(
            await BackofficeStateStore.GetAsync(_db, BackofficeStateKeys.ConsentsBackfilledUntil, cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var stored)
            ? stored
            : DateTimeOffset.MinValue;

        var records = await _db.ConsentRecords.AsNoTracking()
            .Where(c => c.OutboxItemId == null && c.OccurredAt > from && c.OccurredAt <= cutoff)
            .OrderBy(c => c.OccurredAt)
            .ToListAsync(cancellationToken);

        var batches = 0;
        foreach (var decision in records.GroupBy(r => (r.UserId, r.OccurredAt, r.Source)))
        {
            foreach (var chunk in decision.Chunk(ConsentRequirements.MaxEventsPerBatch))
            {
                var flow = decision.Key.Source == ConsentSources.Signup ? ConsentService.Flows.Signup : ConsentService.Flows.Reconsent;
                _outbox.Enqueue(ConsentRequirements.OutboxKind,
                    ConsentService.Batch(chunk, ip: null, flow, backfill: true),
                    critical: false,
                    subjectRef: chunk[0].SubjectRef);
                batches++;
            }
        }

        // Saves the outbox items together with the new watermark.
        await BackofficeStateStore.SetAsync(_db, BackofficeStateKeys.ConsentsBackfilledUntil, cutoff.ToString("O"), cutoff, cancellationToken);
        return new ConsentBackfillResult(records.Count, batches);
    }
}
