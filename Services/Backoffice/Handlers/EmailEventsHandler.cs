using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// E-mail events (Email module On):
/// <list type="bullet">
/// <item><c>email.bounced</c>, <c>email.complained</c>: sets <c>User.EmailBouncedAt</c> (shown in
/// <c>/Admin/Users/{id}</c>). The user is found by the <c>user_id</c> tag the product sent with
/// the message, else by the recipient address.</item>
/// <item><c>email.failed</c>: logged.</item>
/// </list>
/// Idempotent: a repeated event sets the same time again.
/// </summary>
public sealed class EmailEventsHandler : IBackofficeEventHandler
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailEventsHandler> _logger;

    public EmailEventsHandler(ApplicationDbContext db, TimeProvider time, ILogger<EmailEventsHandler> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } =
    [
        BackofficeEventTypes.EmailBounced,
        BackofficeEventTypes.EmailComplained,
        BackofficeEventTypes.EmailFailed,
    ];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var emailId = String(evt.Data, "email_id");
        var template = Tag(evt.Data, "template");

        if (evt.Type == BackofficeEventTypes.EmailFailed)
        {
            _logger.LogWarning(
                "Backoffice e-mail {EmailId} ({Template}) failed: {Status}",
                emailId, template, String(evt.Data, "status"));
            return;
        }

        var userId = Guid.TryParse(Tag(evt.Data, "user_id"), out var id) ? id : (Guid?)null;
        var user = userId is { } uid
            ? await _db.Users.FirstOrDefaultAsync(u => u.Id == uid, cancellationToken)
            : null;
        if (user == null && String(evt.Data, "to") is { Length: > 0 } to)
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == to, cancellationToken);
        }
        if (user == null)
        {
            _logger.LogInformation("Backoffice event {EventId} ({EventType}): no matching user", evt.Id, evt.Type);
            return;
        }

        var occurredAt = DateTimeOffset.TryParse(String(evt.Data, "occurred_at"), out var at) ? at : _time.GetUtcNow();
        if (user.EmailBouncedAt == null || occurredAt > user.EmailBouncedAt)
        {
            user.EmailBouncedAt = occurredAt.ToUniversalTime();
            await _db.SaveChangesAsync(cancellationToken);
        }
        _logger.LogWarning(
            "Backoffice e-mail {EmailId} ({Template}) to user {UserId}: {EventType}",
            emailId, template, user.Id, evt.Type);
    }

    private static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Tag(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("tags", out var tags)
        && tags.ValueKind == JsonValueKind.Object
            ? String(tags, name)
            : null;
}
