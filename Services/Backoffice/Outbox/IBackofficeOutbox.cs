namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Queues a write to the P4 Backoffice service as part of the caller's unit of work.
/// </summary>
public interface IBackofficeOutbox
{
    /// <summary>
    /// Adds an outbox item to the scoped <c>ApplicationDbContext</c> and returns its id (also
    /// the request's <c>Idempotency-Key</c>). Does <b>not</b> save: the item is written by the
    /// caller's own <c>SaveChangesAsync</c>, together with the domain change, and the dispatcher
    /// is woken only once that is committed.
    /// </summary>
    /// <param name="kind">Handler key (≤ 40 characters), e.g. <c>consent</c>.</param>
    /// <param name="payload">Serialised to JSON (snake_case) and encrypted.</param>
    /// <param name="critical">Sent before non-critical items.</param>
    /// <param name="subjectRef">What the item is about, for lookups (≤ 200, never secret).</param>
    Guid Enqueue(string kind, object payload, bool critical = false, string? subjectRef = null);
}
