using System.Text;
using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Support;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>support.ticket</c> to <c>POST /v1/support/tickets</c> with the item id as
/// <c>Idempotency-Key</c>. <c>201</c> (ticket created) and <c>202</c> (queued: the service
/// retries itself) are both Sent; <c>RemoteId</c> keeps the request id and the ticket number
/// (<see cref="SupportTicketReceipt"/>). <c>422 support_invalid</c> ends Dead without retries
/// and is logged as an error. Everything else follows <see cref="RetryPolicy"/>.
///
/// Own HTTP call following the service's OpenAPI document — no SDK dependency, so every build
/// (forks included) can use it.
/// </summary>
public sealed class SupportTicketOutboxHandler : JsonPostOutboxHandler
{
    public const string SupportInvalid = "support_invalid";

    private readonly ILogger<SupportTicketOutboxHandler> _logger;

    public SupportTicketOutboxHandler(ILogger<SupportTicketOutboxHandler> logger)
    {
        _logger = logger;
    }

    public override string Kind => SupportTicketRequest.OutboxKind;

    protected override string Path => "v1/support/tickets";

    public override async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        using var message = request.CreateHttpRequest(HttpMethod.Post, Path);
        message.Content = new StringContent(request.PayloadJson, Encoding.UTF8, "application/json");
        using var response = await request.Http.SendAsync(message, cancellationToken);

        // Read twice (result + ticket fields): buffer first.
        await response.Content.LoadIntoBufferAsync();
        var result = await ReadResultAsync(response, cancellationToken);

        if (result.StatusCode is >= 200 and < 300)
        {
            var (status, ticketNumber) = await AcceptedAsync(response, cancellationToken);
            if (status is "failed" or "dead" or "spam")
            {
                _logger.LogWarning(
                    "Backoffice support ticket {ItemId} ({RequestId}) was accepted with status {Status}; "
                    + "the service reports the outcome as support.ticket_failed",
                    request.IdempotencyKey, result.RemoteId, status);
            }
            return result with { RemoteId = SupportTicketReceipt.Format(result.RemoteId, ticketNumber) };
        }

        if (result.StatusCode == 422 && result.ProblemCode == SupportInvalid)
        {
            _logger.LogError(
                "Backoffice support ticket {ItemId}: the service refused the request (422 {Code}); the item is Dead. "
                + "Compare SupportTicketRequest with SupportTicketInput of the service",
                request.IdempotencyKey, SupportInvalid);
        }
        return result;
    }

    private static async Task<(string? Status, string? TicketNumber)> AcceptedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (String(doc.RootElement, "status"), String(doc.RootElement, "ticket_number"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
