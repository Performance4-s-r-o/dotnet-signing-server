namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Sends one kind of outbox item to the service (<c>consent</c>, <c>email.raw</c>, …).
/// Registered as a singleton <see cref="IOutboxHandler"/>; the dispatcher picks it by <see cref="Kind"/>.
///
/// A handler returns what the service answered and leaves the decision (sent, retry,
/// blocked, dead) to <see cref="RetryPolicy"/>. Network errors and timeouts may simply be
/// thrown. It must send <see cref="OutboxRequest.IdempotencyKey"/> as <c>Idempotency-Key</c>
/// on every attempt — <see cref="OutboxRequest.CreateHttpRequest"/> does that.
/// </summary>
public interface IOutboxHandler
{
    string Kind { get; }

    /// <summary>
    /// True when the service recognises a repeat of this kind by its content (a once-only key in
    /// the payload), not only by <c>Idempotency-Key</c>, which it keeps 24 h. Only such kinds are
    /// retried past <see cref="RetryPolicy.GiveUpAfter"/> (up to <see cref="RetryPolicy.DeduplicatedGiveUpAfter"/>).
    /// </summary>
    bool ServiceDeduplicates => false;

    /// <summary>
    /// A give-up time of this kind's own, overriding the configured one and
    /// <see cref="ServiceDeduplicates"/>; null = those apply. For kinds that must not end quietly
    /// within a day — an erasure is retried for 30 days (#217) — and that the service accepts
    /// any number of times (it recognises the subject, whatever the <c>Idempotency-Key</c>).
    /// </summary>
    TimeSpan? GiveUpAfter => null;

    /// <summary>
    /// From this age of the item on, every failed attempt is logged as an error starting with
    /// <see cref="OutboxProcessor.AlertPrefix"/> instead of a warning; null = never.
    /// </summary>
    TimeSpan? AlertAfter => null;

    /// <summary>
    /// Shorter give-up time for <c>critical</c> items whose content expires (a 2FA code, a reset link):
    /// after it the item is Dead with its payload dropped instead of being sent too late. Null = the kind's usual one.
    /// </summary>
    TimeSpan? CriticalGiveUpAfter => null;

    Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken);
}

/// <summary>One attempt at sending an outbox item.</summary>
public sealed class OutboxRequest
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public OutboxRequest(HttpClient http, Guid idempotencyKey, string kind, string payloadJson, int attempt, bool critical, string? subjectRef)
    {
        Http = http;
        IdempotencyKey = idempotencyKey;
        Kind = kind;
        PayloadJson = payloadJson;
        Attempt = attempt;
        Critical = critical;
        SubjectRef = subjectRef;
    }

    /// <summary>
    /// Client <c>"P4Backoffice.Outbox"</c>: base address = service root (paths are relative,
    /// e.g. <c>v1/emails</c>), <c>Authorization: Bearer</c>, 10 s timeout.
    ///
    /// Once handlers use the SDK: <c>BackofficeClientFactory.Create(request.Http, baseUrl, key)</c>
    /// in <c>Services/Backoffice/Sdk</c>, with the <c>Idempotency-Key</c> header added per request.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>The item's id.</summary>
    public Guid IdempotencyKey { get; }

    public string Kind { get; }

    /// <summary>Decrypted payload (JSON).</summary>
    public string PayloadJson { get; }

    /// <summary>1 for the first attempt.</summary>
    public int Attempt { get; }

    public bool Critical { get; }

    public string? SubjectRef { get; }

    /// <summary>A request to <paramref name="path"/> (relative to the service root) carrying the idempotency key.</summary>
    public HttpRequestMessage CreateHttpRequest(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path.TrimStart('/'));
        message.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, IdempotencyKey.ToString());
        return message;
    }
}
