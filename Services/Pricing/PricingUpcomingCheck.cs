using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Safety net for a lost <c>price.scheduled</c> (Pricing Shadow/On): once a day asks
/// <c>GET /v1/pricing/upcoming</c> for the next scheduled price-list version. When no user has the
/// notice of that version yet although it takes effect in less than its <c>notice_days</c> (so
/// the event should have arrived), it runs <see cref="PriceScheduledHandler"/> with a synthetic
/// event built from the upcoming prices and the prices in force, and logs a warning. The
/// handler's per-user idempotence makes a late webhook after this harmless.
///
/// Background only; a failure is logged and retried later.
/// </summary>
public sealed class PricingUpcomingCheck : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Id of the synthetic event: this prefix and the version.</summary>
    public const string EventIdPrefix = "pricing-upcoming:v";

    private readonly BackofficePricingClient _client;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PricingUpcomingCheck> _logger;

    public PricingUpcomingCheck(BackofficePricingClient client, IServiceScopeFactory scopes, TimeProvider time, ILogger<PricingUpcomingCheck> logger)
    {
        _client = client;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstRunDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = Interval;
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                delay = RetryAfterFailure;
                _logger.LogWarning(ex, "[pricing] check of upcoming price lists failed; next try in {Delay}", delay);
            }
        }
    }

    /// <summary>One check; null when nothing had to be done.</summary>
    public async Task<PriceNoticeResult?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var body = await _client.GetUpcomingAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (PriceEventData.Object(document.RootElement, "upcoming") is not { } upcoming)
        {
            return null;
        }
        if (PriceEventData.Int(upcoming, "version") is not { } version
            || PriceEventData.Date(upcoming, "effective_from") is not { } effectiveFrom)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var noticeDays = PriceEventData.Int(upcoming, "notice_days") ?? 0;
        // In force already: price.effective (or the hourly refresh) takes over. Too early: the
        // service sends price.scheduled notice_days ahead, so it is not late yet.
        if (effectiveFrom <= now || effectiveFrom - now >= TimeSpan.FromDays(noticeDays))
        {
            return null;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.Users.AnyAsync(u => u.PriceChangeNotifiedVersion == version, cancellationToken))
        {
            return null;
        }

        var pricing = scope.ServiceProvider.GetRequiredService<ICreditPricingProvider>();
        var currency = PriceScheduledHandler.ProductCurrency(scope.ServiceProvider.GetRequiredService<IOptionsMonitor<StripeOptions>>().CurrentValue);
        var changes = Changes(PriceListMapper.Map(upcoming, currency), pricing.GetPacks(), currency);
        var data = new PriceChangeEvent(version, effectiveFrom, noticeDays, changes, []);

        var handler = scope.ServiceProvider.GetRequiredService<PriceScheduledHandler>();
        var result = await handler.NotifyAsync(data, EventIdPrefix + version, cancellationToken);
        if (result.Planned > 0)
        {
            _logger.LogWarning(
                "[pricing] price.scheduled of v{Version} (effective {EffectiveFrom:u}) was not received; {Count} users due a notice found via /v1/pricing/upcoming",
                version, effectiveFrom, result.Planned);
        }
        return result;
    }

    /// <summary>The credit packs of <paramref name="upcoming"/> whose amount differs from the one in force. Pure.</summary>
    public static IReadOnlyList<PriceChangeEntry> Changes(PriceListMapping upcoming, IReadOnlyList<CreditPack> current, string currency) =>
        upcoming.Packs.Values
            .OrderBy(p => p.Quantity)
            .Select(next => (next, now: current.FirstOrDefault(p => p.Quantity == next.Quantity)))
            .Where(x => x.now is null || x.now.UnitAmountMinor != x.next.UnitAmountMinor)
            .Select(x => new PriceChangeEntry(
                x.next.LookupKey ?? $"credits_{x.next.Quantity}",
                $"credits_{x.next.Quantity}",
                currency,
                "one_time",
                x.now?.UnitAmountMinor,
                x.next.UnitAmountMinor))
            .ToList();
}
