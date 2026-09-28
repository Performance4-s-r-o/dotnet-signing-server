using System.Text;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers;

/// <summary>
/// Webhooks of the P4 Backoffice service (Standard Webhooks). Verifies the signature,
/// stores the event in the inbox and answers 200 — nothing else happens in the request;
/// <see cref="BackofficeInboxProcessor"/> runs the handlers in the background.
///
/// 404 while the integration is Off or no webhook secret is set; removed from routing on a
/// PrivateServer (<see cref="Conventions.PrivateServerConvention"/>). Not an
/// <see cref="ApiControllerBase"/>, so it stays out of the public API docs.
/// </summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class BackofficeWebhookController : ControllerBase
{
    public const string Route = "/api/webhooks/p4";
    public const int MaxBodyBytes = 256 * 1024;

    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly BackofficeInbox _inbox;
    private readonly BackofficeInboxSignal _signal;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficeWebhookController> _logger;

    public BackofficeWebhookController(
        IOptions<P4BackofficeProductOptions> options,
        BackofficeInbox inbox,
        BackofficeInboxSignal signal,
        TimeProvider time,
        ILogger<BackofficeWebhookController> logger)
    {
        _options = options;
        _inbox = inbox;
        _signal = signal;
        _time = time;
        _logger = logger;
    }

    [HttpPost(Route)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.AnyEnabled || !options.Webhook.Configured)
        {
            return NotFound();
        }

        // The signature covers the exact bytes, so read the raw body rather than a model.
        Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        VerifiedWebhook message;
        try
        {
            var verifier = new StandardWebhookVerifier(new[] { options.Webhook.Secret, options.Webhook.PreviousSecret });
            message = verifier.Verify(body, h => Request.Headers[h].FirstOrDefault(), _time.GetUtcNow());
        }
        catch (BackofficeWebhookVerificationException ex)
        {
            // The body is never logged: it is unauthenticated input.
            _logger.LogWarning("Backoffice webhook rejected: {Reason}", ex.Message);
            return BadRequest(new { error = "Invalid webhook" });
        }

        if (!BackofficeInbox.IsStorable(message.Id, message.Type))
        {
            _logger.LogWarning("Backoffice webhook rejected: id or type too long ({EventType})", message.Type);
            return BadRequest(new { error = "Invalid webhook" });
        }

        var added = await _inbox.AddIfNewAsync(message.Id, message.Type, message.Data, BackofficeInboxSource.Webhook, cancellationToken);
        if (added)
        {
            _signal.Notify();
        }
        else
        {
            _logger.LogDebug("Backoffice webhook {EventId} already received", message.Id);
        }
        return Ok();
    }
}
