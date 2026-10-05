using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>email.template</c> to <c>POST /v1/emails</c> in template mode
/// (<c>to</c>, <c>template</c>, <c>variables</c>, <c>locale</c>, <c>tags</c>) with the item id as
/// <c>Idempotency-Key</c>. The break-glass copy (<c>fallback</c>) is removed from the request.
/// Answers are handled as for <c>email.raw</c>; <c>422 template_variables_invalid</c> (the
/// template's schema does not match what the product sends) ends Dead without retries and is
/// logged as an error naming the template.
///
/// Own HTTP call following the service's OpenAPI document — no SDK dependency, so every build
/// (forks included) can use it.
/// </summary>
public sealed class EmailTemplateOutboxHandler : EmailRawOutboxHandler
{
    public const string TemplateVariablesInvalid = "template_variables_invalid";

    private readonly ILogger<EmailTemplateOutboxHandler> _logger;

    public EmailTemplateOutboxHandler(ILogger<EmailTemplateOutboxHandler> logger)
    {
        _logger = logger;
    }

    public override string Kind => TemplatedEmailSender.OutboxKind;

    public override async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        var result = await base.SendAsync(request, cancellationToken);
        if (result.StatusCode == 422 && result.ProblemCode == TemplateVariablesInvalid)
        {
            _logger.LogError(
                "Backoffice e-mail {ItemId}: the service refused the variables of template {TemplateKey} "
                + "(422 {Code}); the item is Dead. Compare the template's schema in the service with "
                + "EmailTemplateVariables, or remove the key from P4Backoffice:Email:TemplateKeys",
                request.IdempotencyKey, TemplateKey(request.PayloadJson), TemplateVariablesInvalid);
        }
        return result;
    }

    /// <summary>The stored payload without the local break-glass copy.</summary>
    protected override string RequestBody(OutboxRequest request) => WithoutFallback(request.PayloadJson);

    public static string WithoutFallback(string payloadJson)
    {
        if (JsonNode.Parse(payloadJson) is not JsonObject body) return payloadJson;
        body.Remove(EmailTemplatePayload.FallbackProperty);
        return body.ToJsonString();
    }

    private static string? TemplateKey(string payloadJson)
    {
        try
        {
            return JsonNode.Parse(payloadJson)?["template"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
