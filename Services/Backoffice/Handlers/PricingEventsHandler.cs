using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>price.effective</c> (Pricing Shadow/On): a new price list is in force and its lookup keys
/// point to the new Stripe Prices. Fetches the price list right away and drops the cached
/// Stripe Prices. A failed fetch throws, so the inbox retries the event; the hourly refresh
/// catches up in any case.
///
/// The other <c>price.*</c> events (notices to customers) are not handled here yet.
/// </summary>
public sealed class PricingEventsHandler : IBackofficeEventHandler
{
    private readonly PricingSnapshotRefresher _refresher;
    private readonly StripePriceResolver _resolver;
    private readonly ILogger<PricingEventsHandler> _logger;

    public PricingEventsHandler(PricingSnapshotRefresher refresher, StripePriceResolver resolver, ILogger<PricingEventsHandler> logger)
    {
        _refresher = refresher;
        _resolver = resolver;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.PriceEffective];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        _resolver.Invalidate();
        try
        {
            await _refresher.RefreshAsync(BackofficeEventTypes.PriceEffective, conditional: false, cancellationToken);
        }
        finally
        {
            // Again after the fetch: a checkout in between may have cached the old Price.
            _resolver.Invalidate();
        }
        _logger.LogInformation("Backoffice event {EventId}: price list refreshed after {EventType}", evt.Id, evt.Type);
    }
}
