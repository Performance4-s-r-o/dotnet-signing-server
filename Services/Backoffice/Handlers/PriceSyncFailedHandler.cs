using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>price.sync_failed</c>: the service could not sync a price-list version to Stripe, so the
/// version does not take effect until a sync succeeds. Logged as an error; nothing changes here.
/// </summary>
public sealed class PriceSyncFailedHandler : IBackofficeEventHandler
{
    private readonly ILogger<PriceSyncFailedHandler> _logger;

    public PriceSyncFailedHandler(ILogger<PriceSyncFailedHandler> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.PriceSyncFailed];

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var data = evt.Data;
        var error = PriceEventData.Object(data, "error");
        _logger.LogError(
            "[pricing] Stripe sync of price list v{Version} failed ({EventId}): {ErrorCode} {ErrorMessage}; attempts {Attempts}, "
            + "next attempt {NextAttemptAt}, effective from {EffectiveFrom}. The version does not take effect until the sync succeeds",
            PriceEventData.Int(data, "version"), evt.Id,
            error is { } e1 ? PriceEventData.String(e1, "code") : null,
            error is { } e2 ? PriceEventData.String(e2, "message") : null,
            PriceEventData.Int(data, "attempts"),
            PriceEventData.String(data, "next_attempt_at"),
            PriceEventData.String(data, "effective_from"));
        return Task.CompletedTask;
    }
}
