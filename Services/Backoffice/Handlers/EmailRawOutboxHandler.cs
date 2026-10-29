using System.Text;
using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>email.raw</c> to <c>POST /v1/emails</c>: the stored
/// <see cref="EmailRawPayload"/> (<c>to</c>, <c>subject</c>, <c>html</c>, <c>tags</c>,
/// <c>critical</c>, <c>category=transactional</c>) as is, with the item id as
/// <c>Idempotency-Key</c>. <c>202</c> with <c>status=queued</c> ⇒ Sent (the service's id kept
/// as <c>RemoteId</c>); <c>status=suppressed</c> or <c>422 suppressed_recipient</c> ⇒ Dead
/// without an alert.
///
/// Own HTTP call following the service's OpenAPI document — no SDK dependency, so every build
/// (forks included) can use it.
/// </summary>
public class EmailRawOutboxHandler : JsonPostOutboxHandler, IOutboxHandler
{
    public const string SuppressedStatus = "suppressed";

    /// <summary>A message not accepted this long after it was queued raises an ops alert.</summary>
    public static readonly TimeSpan MailAlertAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Critical e-mail (2FA code, reset or verification link) is not sent later than this: the
    /// user has asked again by then, and the code or link is no use. Shorter than <c>Outbox:GiveUpAfter</c>.
    /// </summary>
    public static readonly TimeSpan CriticalExpiry = TimeSpan.FromHours(1);

    public TimeSpan? AlertAfter => MailAlertAfter;

    public TimeSpan? CriticalGiveUpAfter => CriticalExpiry;

    public override string Kind => BackofficeOutboxEmailSender.OutboxKind;

    protected override string Path => "v1/emails";

    public override async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        using var message = request.CreateHttpRequest(HttpMethod.Post, Path);
        message.Content = new StringContent(RequestBody(request), Encoding.UTF8, "application/json");
        using var response = await request.Http.SendAsync(message, cancellationToken);

        // Read once: ReadResultAsync consumes the content, so buffer it first.
        await response.Content.LoadIntoBufferAsync();
        var result = await ReadResultAsync(response, cancellationToken);
        if (result.StatusCode is >= 200 and < 300
            && await StatusAsync(response, cancellationToken) == SuppressedStatus)
        {
            return OutboxAttemptResult.Suppressed(result.StatusCode.Value, result.RemoteId);
        }
        return result;
    }

    /// <summary>JSON sent to the service; the stored payload as is.</summary>
    protected virtual string RequestBody(OutboxRequest request) => request.PayloadJson;

    private static async Task<string?> StatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
