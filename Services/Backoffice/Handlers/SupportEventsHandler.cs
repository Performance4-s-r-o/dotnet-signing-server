using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Inbox;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>support.ticket_failed</c>: the service gave up on a ticket (refused by the helpdesk or not
/// delivered in time). Logged as an error with the request id and the user reference so an
/// admin can follow up; the user is not e-mailed (yet).
/// </summary>
public sealed class SupportEventsHandler : IBackofficeEventHandler
{
    private readonly ILogger<SupportEventsHandler> _logger;

    public SupportEventsHandler(ILogger<SupportEventsHandler> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.SupportTicketFailed];

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var data = evt.Data;
        _logger.LogError(
            "[support] Ticket request {RequestId} ({Category}, {SubjectRef}) failed ({EventId}): status {Status}, reason {Reason}. "
            + "The user was told the request was received; follow up in the service admin",
            String(data, "id"), String(data, "category"), String(data, "subject_ref"), evt.Id,
            String(data, "status"), String(data, "reason"));
        return Task.CompletedTask;
    }

    private static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
