using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>price.unscheduled</c>: an announced price change was taken back. On clears
/// <c>User.PriceChangeNotifiedVersion</c> of the users told about that version, so a new
/// schedule of it notifies them again; Shadow only logs. No e-mail about the withdrawal yet
/// (there is no template for it).
/// </summary>
public sealed class PriceUnscheduledHandler : IBackofficeEventHandler
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<PriceUnscheduledHandler> _logger;
    private readonly BackofficeMode _mode;

    public PriceUnscheduledHandler(ApplicationDbContext db, ILogger<PriceUnscheduledHandler> logger, BackofficeMode mode)
    {
        _db = db;
        _logger = logger;
        _mode = mode;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.PriceUnscheduled];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        if (PriceEventData.Int(evt.Data, "version") is not { } version)
        {
            _logger.LogError("Backoffice event {EventId} ({EventType}) ignored: data.version is missing", evt.Id, evt.Type);
            return;
        }

        var users = await _db.Users.Where(u => u.PriceChangeNotifiedVersion == version).ToListAsync(cancellationToken);
        if (_mode != BackofficeMode.On)
        {
            _logger.LogInformation("[pricing] shadow: price list v{Version} unscheduled ({EventId}); {Count} users were notified",
                version, evt.Id, users.Count);
            return;
        }

        foreach (var user in users)
        {
            user.PriceChangeNotifiedVersion = null;
        }
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "[pricing] price list v{Version} unscheduled ({EventId}); {Count} users had been notified of it and get no withdrawal e-mail",
            version, evt.Id, users.Count);
    }
}
