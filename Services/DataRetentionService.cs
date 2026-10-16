using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Daily retention of data that nothing else cleans (#217, GDPR art. 5(1)(e)):
/// <list type="bullet">
/// <item><c>WebhookEvents</c> (Stripe payloads: customer e-mail, address) after
/// <see cref="WebhookEventRetention"/> — Stripe stops redelivering after 3 days, so the
/// deduplication they serve is long over. Auto-recharge claims (<c>auto_recharge_claim_*</c>)
/// are not events and are never touched here.</item>
/// <item><c>Documents</c> (who signed when; the files are gone after signing) after
/// <see cref="DocumentRetention"/>. Usage records stay for billing: only their link to the
/// document is cut.</item>
/// </list>
/// The backoffice inbox/outbox are cleaned by their own workers
/// (<c>BackofficeInboxProcessor</c>, <c>OutboxHealth</c>). Invoices and payments stay (10 years).
/// </summary>
public sealed class DataRetentionService(IServiceProvider services, TimeProvider time, ILogger<DataRetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan WebhookEventRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan DocumentRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations check) finish first.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var (events, documents) = await RunAsync(db, time.GetUtcNow(), stoppingToken);
                if (events + documents > 0)
                    logger.LogInformation("Retention removed {Events} webhook event(s) and {Documents} document(s)", events, documents);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Silence would let the data pile up: loud, and tried again tomorrow.
                logger.LogError(ex, "[retention] cleanup failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public static async Task<(int Events, int Documents)> RunAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var eventCutoff = now - WebhookEventRetention;
        var claimPrefix = AutoRechargeClaim.EventIdFor(Guid.Empty)[..^36];
        var oldEvents = db.WebhookEvents.Where(w => w.ReceivedAt < eventCutoff && !w.EventId.StartsWith(claimPrefix));

        var docCutoff = now - DocumentRetention;
        var oldDocuments = db.Documents.Where(d => d.CreatedAt < docCutoff);

        if (db.Database.IsRelational())
        {
            var events = await oldEvents.ExecuteDeleteAsync(ct);
            await db.UsageRecords.Where(u => u.DocumentId != null && oldDocuments.Select(d => d.Id).Contains(u.DocumentId!.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DocumentId, (Guid?)null), ct);
            var documents = await oldDocuments.ExecuteDeleteAsync(ct);
            return (events, documents);
        }

        var eventRows = await oldEvents.ToListAsync(ct);
        db.WebhookEvents.RemoveRange(eventRows);
        var docRows = await oldDocuments.ToListAsync(ct);
        var ids = docRows.Select(d => d.Id).ToHashSet();
        foreach (var u in await db.UsageRecords.Where(u => u.DocumentId != null).ToListAsync(ct))
            if (ids.Contains(u.DocumentId!.Value)) u.DocumentId = null;
        db.Documents.RemoveRange(docRows);
        await db.SaveChangesAsync(ct);
        return (eventRows.Count, docRows.Count);
    }
}
