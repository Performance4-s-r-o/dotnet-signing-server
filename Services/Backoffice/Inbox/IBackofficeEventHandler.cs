using System.Text.Json;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>An event taken from the inbox.</summary>
/// <param name="Id">Dedupe key (<c>webhook-id</c> / event <c>id</c>).</param>
/// <param name="Data">The event's <c>data</c>.</param>
/// <param name="Source"><c>webhook</c> or <c>poll</c>.</param>
public sealed record BackofficeEvent(string Id, string Type, JsonElement Data, string Source, DateTimeOffset ReceivedAt, int Attempt);

/// <summary>
/// Handles one or more event types (<c>price.*</c>, <c>document.*</c>, …). Registered as a
/// scoped <see cref="IBackofficeEventHandler"/>; <see cref="EventHandlerRegistry"/> picks it
/// by type. Runs in the background, never in a request.
///
/// Must be idempotent: the inbox stores each event once, but a handler may run again for
/// the same event after a crash or a failed attempt. Throwing schedules a retry.
///
/// Exception messages must not carry event data or personal data (e-mail addresses, names,
/// document contents): the processor stores the message in the inbox row's <c>Error</c>
/// column and logs the exception. Name the event by <see cref="BackofficeEvent.Id"/> and the
/// failing step instead. E-mail addresses are masked in <c>Error</c> as a safety net only.
/// </summary>
public interface IBackofficeEventHandler
{
    IReadOnlyCollection<string> Types { get; }

    Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in for the subscribed types no module handles yet, and the handler of
/// <c>webhook.test</c>: logs the event and lets it be marked processed.
/// </summary>
public sealed class LoggingBackofficeEventHandler : IBackofficeEventHandler
{
    private readonly ILogger<LoggingBackofficeEventHandler> _logger;

    public LoggingBackofficeEventHandler(ILogger<LoggingBackofficeEventHandler> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } =
        BackofficeEventTypes.Subscribed.Append(BackofficeEventTypes.WebhookTest).ToArray();

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Type == BackofficeEventTypes.WebhookTest)
        {
            _logger.LogInformation("Backoffice webhook test received ({EventId})", evt.Id);
        }
        else
        {
            _logger.LogInformation("Backoffice event {EventType} {EventId} ({Source}) has no handler yet; marked processed",
                evt.Type, evt.Id, evt.Source);
        }
        return Task.CompletedTask;
    }
}
