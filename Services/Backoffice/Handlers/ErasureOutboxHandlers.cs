using System.Text;
using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Erasure;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>subject.erase</c> to <c>POST /v1/erasures</c> (#217): the stored
/// <see cref="ErasureRequestPayload"/> as is, the item id as <c>Idempotency-Key</c>.
/// <c>202</c> (new request) and <c>200</c> (the open request of the same subject) are Sent.
///
/// Unlike the other kinds this one is retried for <see cref="AccountErasure.RetryWindow"/>: the
/// service answers a repeat for the same subject with the same request whatever the key, so the
/// 24 h idempotency window does not bound it. From <see cref="AccountErasure.AlertAfter"/> on every
/// failed attempt is an alert; a refusal (4xx, e.g. <c>409 erasure_identifiers_mismatch</c>) and
/// the end of the window are Dead, logged as errors, and kept — never quietly dropped.
///
/// Own HTTP call following the service's OpenAPI document — no SDK dependency.
/// </summary>
// IOutboxHandler is listed again so the two properties below replace the interface's defaults.
public sealed class ErasureRequestOutboxHandler : JsonPostOutboxHandler, IOutboxHandler
{
    public override string Kind => AccountErasure.RequestOutboxKind;

    public override bool ServiceDeduplicates => true;

    public TimeSpan? GiveUpAfter => AccountErasure.RetryWindow;

    public TimeSpan? AlertAfter => AccountErasure.AlertAfter;

    protected override string Path => "v1/erasures";
}

/// <summary>
/// Sends outbox items <c>subject.erasure.ack</c> to
/// <c>POST /v1/erasures/{id}/targets/{product}/ack</c> (#217) with the item id as
/// <c>Idempotency-Key</c>. <c>409 erasure_target_settled</c> means the target is already closed:
/// Sent only when it was closed with this very status (a repeat of our own ack — the service then
/// usually answers 200 anyway). A <c>failed</c> ack, or one that differs from how the target was
/// closed, is Dead and an alert: the service's record and ours disagree, and a person decides.
/// The closed status is read with <c>GET /v1/erasures/{id}</c>. Same retry window and alerting as
/// <see cref="ErasureRequestOutboxHandler"/>.
/// </summary>
public sealed class ErasureAckOutboxHandler : IOutboxHandler
{
    public const string TargetSettled = "erasure_target_settled";

    private static readonly JsonSerializerOptions Json = BackofficeOutbox.PayloadJson;

    private readonly ILogger<ErasureAckOutboxHandler> _logger;

    public ErasureAckOutboxHandler(ILogger<ErasureAckOutboxHandler> logger)
    {
        _logger = logger;
    }

    public string Kind => AccountErasure.AckOutboxKind;

    public bool ServiceDeduplicates => true;

    public TimeSpan? GiveUpAfter => AccountErasure.RetryWindow;

    public TimeSpan? AlertAfter => AccountErasure.AlertAfter;

    public async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        var ack = JsonSerializer.Deserialize<ErasureAckPayload>(request.PayloadJson, Json);
        if (ack == null || ack.ErasureId == Guid.Empty || !AccountErasure.IsValidProductSlug(ack.Product))
        {
            return OutboxAttemptResult.Fatal("Erasure ack payload is incomplete");
        }

        var path = $"v1/erasures/{ack.ErasureId:D}/targets/{Uri.EscapeDataString(ack.Product)}/ack";
        var body = JsonSerializer.Serialize(new AckBody
        {
            Status = ack.Status,
            Counts = ack.Counts,
            RetainedReason = ack.RetainedReason,
        }, Json);

        using var message = request.CreateHttpRequest(HttpMethod.Post, path);
        message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await request.Http.SendAsync(message, cancellationToken);
        var result = await JsonPostOutboxHandler.ReadResultAsync(response, cancellationToken);

        if (result.StatusCode == 409 && result.ProblemCode == TargetSettled)
        {
            if (ack.Status == AccountErasure.AckStatuses.Failed)
            {
                return result; // Dead + alert: a failure reported for a target someone closed
            }
            var settled = await SettledStatusAsync(request, ack, cancellationToken);
            if (settled.Error is not null) return settled.Error;
            if (settled.Status == ack.Status)
            {
                _logger.LogInformation(
                    "Erasure {ErasureId}: target product:{Product} is already {Status}; ack not needed",
                    ack.ErasureId, ack.Product, ack.Status);
                return OutboxAttemptResult.Response(200, remoteId: ack.ErasureId.ToString("D"));
            }
            _logger.LogError(
                "Erasure {ErasureId}: target product:{Product} was closed as {Settled}, our ack says {Status}; a person has to reconcile",
                ack.ErasureId, ack.Product, settled.Status ?? "unknown", ack.Status);
            return result;
        }
        return result.StatusCode is >= 200 and < 300 ? result with { RemoteId = ack.ErasureId.ToString("D") } : result;
    }

    /// <summary>How the service closed our target; an attempt result when it could not be read (retried or dead).</summary>
    private static async Task<(string? Status, OutboxAttemptResult? Error)> SettledStatusAsync(
        OutboxRequest request, ErasureAckPayload ack, CancellationToken cancellationToken)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, $"v1/erasures/{ack.ErasureId:D}");
        using var response = await request.Http.SendAsync(get, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, await JsonPostOutboxHandler.ReadResultAsync(response, cancellationToken));
        }
        var target = AccountErasure.ProductTargetPrefix + ack.Product;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in targets.EnumerateArray())
                {
                    if (t.TryGetProperty("target", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == target
                        && t.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                    {
                        return (status.GetString(), null);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable: treated as a status that differs.
        }
        return (null, null);
    }

    private sealed class AckBody
    {
        public string Status { get; init; } = "";

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyDictionary<string, int>? Counts { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? RetainedReason { get; init; }
    }
}
