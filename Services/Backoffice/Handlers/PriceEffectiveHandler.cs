using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>price.effective</c>: a new price list is in force (the service sends it after a successful
/// Stripe sync) and its lookup keys point to the new Stripe Prices.
///
/// Shadow and On: fetches the price list right away and drops the cached Stripe Prices. A failed
/// fetch throws, so the inbox retries the event; the hourly refresh catches up in any case.
///
/// On only: stores the new price of 100 credits in <c>User.AutoRechargePricePer100</c> of every
/// user with auto-recharge (auto-recharge itself always charges the pack's current price) and
/// clears the notice markers (<c>PriceChangeNotifiedAt</c>, and <c>PriceChangeNotifiedVersion</c>
/// up to this version). Throws while the fetched price list is older than the event's version.
/// </summary>
public sealed class PriceEffectiveHandler : IBackofficeEventHandler
{
    private readonly PricingSnapshotRefresher _refresher;
    private readonly PricingSnapshotHolder _holder;
    private readonly StripePriceResolver _resolver;
    private readonly ApplicationDbContext _db;
    private readonly IOptionsMonitor<StripeOptions> _stripe;
    private readonly ILogger<PriceEffectiveHandler> _logger;
    private readonly BackofficeMode _mode;

    public PriceEffectiveHandler(
        PricingSnapshotRefresher refresher,
        PricingSnapshotHolder holder,
        StripePriceResolver resolver,
        ApplicationDbContext db,
        IOptionsMonitor<StripeOptions> stripe,
        ILogger<PriceEffectiveHandler> logger,
        BackofficeMode mode)
    {
        _refresher = refresher;
        _holder = holder;
        _resolver = resolver;
        _db = db;
        _stripe = stripe;
        _logger = logger;
        _mode = mode;
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

        if (_mode != BackofficeMode.On)
        {
            return;
        }

        var version = PriceEventData.Int(evt.Data, "version");
        if (version is null)
        {
            _logger.LogError("Backoffice event {EventId} ({EventType}): data.version is missing; stored auto-recharge prices unchanged",
                evt.Id, evt.Type);
            return;
        }
        await ApplyAsync(version.Value, evt.Id, cancellationToken);
    }

    private async Task ApplyAsync(int version, string eventId, CancellationToken cancellationToken)
    {
        var snapshot = _holder.Current;
        if (snapshot is null || snapshot.Version < version)
        {
            throw new InvalidOperationException(
                $"price.effective {eventId}: the fetched price list is v{snapshot?.Version.ToString() ?? "-"}, not v{version} yet");
        }

        var currency = PriceScheduledHandler.ProductCurrency(_stripe.CurrentValue);
        var pack100 = PriceListMapper.Map(snapshot.Body, currency).Packs.GetValueOrDefault(100);
        if (pack100 is null)
        {
            _logger.LogWarning("[pricing] price list v{Version} has no 100 credits price in {Currency}; stored auto-recharge prices unchanged",
                snapshot.Version, currency);
        }

        var users = await _db.Users
            .Where(u => u.AutoRechargeEnabled
                        || u.PriceChangeNotifiedAt != null
                        || (u.PriceChangeNotifiedVersion != null && u.PriceChangeNotifiedVersion <= version))
            .ToListAsync(cancellationToken);
        var repriced = 0;
        foreach (var user in users)
        {
            if (user.AutoRechargeEnabled && pack100 != null && user.AutoRechargePricePer100 != pack100.Amount)
            {
                user.AutoRechargePricePer100 = pack100.Amount;
                repriced++;
            }
            user.PriceChangeNotifiedAt = null;
            // A notice of a later version stays: that change is still ahead.
            if (user.PriceChangeNotifiedVersion <= version)
            {
                user.PriceChangeNotifiedVersion = null;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[pricing] price list v{Version} in force ({EventId}): auto-recharge price per 100 credits {Price} {Currency} stored for {Count} users",
            version, eventId, pack100?.Amount, currency, repriced);
    }
}
