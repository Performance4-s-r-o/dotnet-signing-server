using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Erasure;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>subject.erasure.requested</c> (#217): the subject asked P4 — not this product — to be
/// erased (origin <c>subject_request</c>). The account named by <c>subject_ref</c>
/// (<c>dotnet:user:{id}</c>) is erased exactly as when the user deletes it
/// (<see cref="AccountErasureService"/>), and the ack of the target <c>product:{slug}</c> is
/// queued in the same commit: <c>done</c>, or <c>retained</c> with the reason when invoices,
/// payments or consent records stay.
///
/// Skipped, and marked processed: a ref of another product (not ours to erase), a request
/// without a <c>product:</c> target (nothing to ack — other origins never carry one), and one
/// that waits for the subject's confirmation or was cancelled. A failure — or a refusal (the
/// portal's service account, the last administrator, a recharge running) — queues a <c>failed</c>
/// ack once and throws, so the inbox retries and finally dead-letters it with an alert; the next
/// success acks again. A duplicate
/// delivery runs the erasure again, which finds a tombstone, and acks the same result.
/// </summary>
public sealed class ErasureRequestedHandler : IBackofficeEventHandler
{
    public const string EventType = BackofficeEventTypes.SubjectErasureRequested;

    private readonly AccountErasureService _erasure;
    private readonly ILogger<ErasureRequestedHandler> _logger;

    public ErasureRequestedHandler(AccountErasureService erasure, ILogger<ErasureRequestedHandler> logger)
    {
        _erasure = erasure;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } = [EventType];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var data = evt.Data;
        var erasureId = Guid.TryParse(String(data, "erasure_id"), out var id) ? id : Guid.Empty;
        var subjectRef = String(data, "subject_ref");
        var status = String(data, "status");

        if (erasureId == Guid.Empty)
        {
            throw new InvalidOperationException($"Event {evt.Id}: erasure_id is missing or not a uuid");
        }
        if (status is "verifying" or "canceled")
        {
            _logger.LogInformation("Erasure {ErasureId} ({EventId}) is {Status}; nothing to do", erasureId, evt.Id, status);
            return;
        }

        var product = ProductTarget(data);
        if (product == null)
        {
            _logger.LogWarning("Erasure {ErasureId} ({EventId}) has no product target; nothing to erase or ack here", erasureId, evt.Id);
            return;
        }
        if (!AccountErasure.TryParseSubjectRef(subjectRef, out var userId))
        {
            // Only the prefix is logged: a foreign ref is not ours to keep in a log either.
            _logger.LogWarning(
                "Erasure {ErasureId} ({EventId}) names a subject of another product ({Prefix}…); ignored",
                erasureId, evt.Id, RefPrefix(subjectRef));
            return;
        }

        var ack = new ErasureAckTarget(erasureId, product);
        AccountErasureResult result;
        try
        {
            result = await _erasure.EraseForRequestAsync(userId, ack, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryQueueFailedAckAsync(evt, userId, ack, cancellationToken);
            throw new InvalidOperationException($"Erasure {erasureId} ({evt.Id}): local erasure failed ({ex.GetType().Name})", ex);
        }

        if (result.Status == AccountErasureStatus.Refused)
        {
            // Logged as an error by the service (portal account, last admin) or a warning (a
            // recharge running): never a quiet done. The inbox retries; a person may have to decide.
            await TryQueueFailedAckAsync(evt, userId, ack, cancellationToken);
            throw new InvalidOperationException($"Erasure {erasureId} ({evt.Id}): refused locally ({result.Refusal}); retried");
        }
        if (result.Status == AccountErasureStatus.BillingFailed)
        {
            await TryQueueFailedAckAsync(evt, userId, ack, cancellationToken);
            throw new InvalidOperationException($"Erasure {erasureId} ({evt.Id}): saved cards could not be removed; retried");
        }
    }

    /// <summary>The first failure tells the service (<c>failed</c>); later ones only retry.</summary>
    private async Task TryQueueFailedAckAsync(BackofficeEvent evt, Guid userId, ErasureAckTarget ack, CancellationToken cancellationToken)
    {
        if (evt.Attempt != 1) return;
        try
        {
            await _erasure.QueueFailedAckAsync(userId, ack, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Erasure {ErasureId} ({EventId}): the failed ack could not be queued", ack.ErasureId, evt.Id);
        }
    }

    /// <summary>The slug of the one <c>product:{slug}</c> target, when it is a valid slug.</summary>
    internal static string? ProductTarget(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("targets", out var targets)
            || targets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var target in targets.EnumerateArray())
        {
            var name = String(target, "target");
            if (name is null || !name.StartsWith(AccountErasure.ProductTargetPrefix, StringComparison.Ordinal)) continue;
            var slug = name[AccountErasure.ProductTargetPrefix.Length..];
            return AccountErasure.IsValidProductSlug(slug) ? slug : null;
        }
        return null;
    }

    private static string RefPrefix(string? subjectRef)
    {
        if (string.IsNullOrEmpty(subjectRef)) return "";
        var colon = subjectRef.IndexOf(':');
        return colon > 0 && colon <= 16 ? subjectRef[..(colon + 1)] : "?";
    }

    private static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
