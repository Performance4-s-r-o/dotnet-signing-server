using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>Outcome of one <c>price.scheduled</c>.</summary>
/// <param name="Planned">Users that were due a notice.</param>
/// <param name="Notified">Notices queued or sent (0 in Shadow).</param>
public sealed record PriceNoticeResult(int Planned, int Notified);

/// <summary>
/// <c>price.scheduled</c>: sends <c>price_change_notice</c> to every user with auto-recharge on a
/// credit pack whose price changes (<see cref="PriceNoticePlanner"/>) and stores the version in
/// <c>User.PriceChangeNotifiedVersion</c> in the same <c>SaveChanges</c> as the queued e-mail
/// (Email=On), or right after the direct send (Email Off/Shadow). One notice per version and user,
/// whether the event came by webhook, polling or <see cref="PricingUpcomingCheck"/>.
///
/// Pricing=Shadow only logs whom it would notify: <see cref="PriceChangeMonitorService"/> still
/// sends the notices then. A failed send throws after the others went out, so the inbox retries
/// just the rest.
/// </summary>
public sealed class PriceScheduledHandler : IBackofficeEventHandler
{
    private readonly ApplicationDbContext _db;
    private readonly ITemplatedEmailSender _email;
    private readonly ICreditPricingProvider _pricing;
    private readonly IOptionsMonitor<StripeOptions> _stripe;
    private readonly IOptions<AppOptions> _app;
    private readonly TimeProvider _time;
    private readonly ILogger<PriceScheduledHandler> _logger;
    private readonly BackofficeMode _mode;

    public PriceScheduledHandler(
        ApplicationDbContext db,
        ITemplatedEmailSender email,
        ICreditPricingProvider pricing,
        IOptionsMonitor<StripeOptions> stripe,
        IOptions<AppOptions> app,
        TimeProvider time,
        ILogger<PriceScheduledHandler> logger,
        BackofficeMode mode)
    {
        _db = db;
        _email = email;
        _pricing = pricing;
        _stripe = stripe;
        _app = app;
        _time = time;
        _logger = logger;
        _mode = mode;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.PriceScheduled];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var data = PriceChangeEvent.TryParse(evt.Data, out var problem);
        if (data is null)
        {
            // Retrying would not fix the payload.
            _logger.LogError("Backoffice event {EventId} ({EventType}) ignored: {Problem}", evt.Id, evt.Type, problem);
            return;
        }
        await NotifyAsync(data, evt.Id, cancellationToken);
    }

    /// <summary>Plans and sends the notices of <paramref name="data"/>.</summary>
    public async Task<PriceNoticeResult> NotifyAsync(PriceChangeEvent data, string eventId, CancellationToken cancellationToken)
    {
        var currency = ProductCurrency(_stripe.CurrentValue);
        var currentPacks = _pricing.GetPacks();
        var changes = PriceNoticePlanner.CreditChanges(data, currency, currentPacks);
        if (changes.Count == 0)
        {
            _logger.LogInformation("[pricing] price list v{Version} ({EventId}) changes no credit pack in {Currency}; no notices",
                data.Version, eventId, currency);
            return new PriceNoticeResult(0, 0);
        }

        var quantities = changes.Keys.ToList();
        var users = await _db.Users
            .Where(u => u.AutoRechargeEnabled && !u.IsEnterprise && quantities.Contains(u.AutoRechargeQuantity))
            .ToListAsync(cancellationToken);
        var plan = PriceNoticePlanner.Plan(data, currency, currentPacks,
            users.Select(u => new PriceNoticeCandidate(u.Id, u.AutoRechargeEnabled, u.AutoRechargeQuantity, u.IsEnterprise, u.PriceChangeNotifiedVersion)));
        if (plan.Problems.Count > 0)
        {
            _logger.LogWarning("[pricing] price list v{Version} ({EventId}): {Problems}", data.Version, eventId, string.Join("; ", plan.Problems));
        }

        if (_mode != BackofficeMode.On)
        {
            _logger.LogInformation(
                "[pricing] shadow: price list v{Version} ({EventId}) would notify {Count} users (packs {Packs}); the price-change monitor sends the notices",
                data.Version, eventId, plan.Notices.Count, string.Join(", ", quantities));
            return new PriceNoticeResult(plan.Notices.Count, 0);
        }

        var now = _time.GetUtcNow();
        var daysNotice = PriceNoticePlanner.DaysUntil(now, data.EffectiveFrom);
        if (daysNotice < data.NoticeDays)
        {
            _logger.LogWarning("[pricing] price list v{Version}: notices go out {Days} days before it takes effect ({NoticeDays} expected)",
                data.Version, daysNotice, data.NoticeDays);
        }

        var byId = users.ToDictionary(u => u.Id);
        var baseUrl = _app.Value.BaseUrl;
        var notified = 0;
        var failed = 0;
        foreach (var notice in plan.Notices)
        {
            var user = byId[notice.UserId];
            var locale = user.EmailLocale;
            var variables = EmailTemplateVariables.PriceChangeNotice(
                daysNotice: daysNotice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                quantity: notice.Change.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                oldPrice: PriceNoticePlanner.FormatMinor(notice.Change.OldMinor),
                newPrice: PriceNoticePlanner.FormatMinor(notice.Change.NewMinor),
                currency: notice.Change.Currency,
                cancelUrl: $"{baseUrl}/Billing/AutoRecharge/Cancel?token={Uri.EscapeDataString(user.AutoRechargeCancelToken ?? "")}",
                billingUrl: $"{baseUrl}/Billing");
            var options = new EmailSendOptions(EmailTemplateId.PriceChangeNotice, locale, user.Id);

            // Email=On: the outbox row and the version are saved together.
            if (_email.TryEnqueue(EmailTemplateId.PriceChangeNotice, user.Email, locale, variables, options))
            {
                user.PriceChangeNotifiedVersion = data.Version;
                await _db.SaveChangesAsync(cancellationToken);
                notified++;
                continue;
            }

            try
            {
                await _email.SendAsync(EmailTemplateId.PriceChangeNotice, user.Email, locale, variables, options);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "[pricing] price change notice of v{Version} to user {UserId} failed", data.Version, user.Id);
                continue;
            }
            user.PriceChangeNotifiedVersion = data.Version;
            await _db.SaveChangesAsync(cancellationToken);
            notified++;
        }

        _logger.LogInformation(
            "[pricing] price list v{Version} ({EventId}): {Notified} price change notices, {Already} already notified, {Failed} failed",
            data.Version, eventId, notified, plan.AlreadyNotified, failed);
        if (failed > 0)
        {
            throw new InvalidOperationException($"price.scheduled {eventId}: {failed} price change notices failed; retrying the rest");
        }
        return new PriceNoticeResult(plan.Notices.Count, notified);
    }

    /// <summary>The currency the product sells in, as the price-list provider maps it.</summary>
    internal static string ProductCurrency(StripeOptions stripe) => ConfigCreditPricingProvider.NormalizeCurrency(stripe.Currency);
}
