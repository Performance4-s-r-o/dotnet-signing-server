using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Background service that checks once per day whether the current price of 100 credits
/// (<see cref="ICreditPricingProvider"/>: <c>Billing:PricePer100</c>) has changed compared to users' stored AutoRechargePricePer100.
/// If a change is detected, users are notified 30 days in advance
/// and given the option to cancel auto-recharge before the new price takes effect.
///
/// Registered only while <c>P4Backoffice:Modules:Pricing</c> is not On; On replaces it by the
/// <c>price.*</c> events (<see cref="Backoffice.Handlers.PriceScheduledHandler"/>).
/// </summary>
public class PriceChangeMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PriceChangeMonitorService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromDays(30);

    public PriceChangeMonitorService(IServiceProvider serviceProvider, ILogger<PriceChangeMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial check by 2 minutes to let the app fully start
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPriceChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during price change monitoring");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckPriceChangesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var billingOptions = scope.ServiceProvider.GetRequiredService<IOptions<BillingOptions>>().Value;
        var email = scope.ServiceProvider.GetRequiredService<ITemplatedEmailSender>();
        var appOptions = scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>().Value;
        var pricing = scope.ServiceProvider.GetRequiredService<ICreditPricingProvider>();

        var currentPrice = pricing.PricePer100;

        // Find users with auto-recharge enabled whose stored price differs from the current one
        // and who haven't been notified yet (or were notified more than 30 days ago)
        var affectedUsers = await dbContext.Users
            .Where(u => u.AutoRechargeEnabled
                && u.AutoRechargeQuantity > 0
                && u.AutoRechargePricePer100 != currentPrice
                && (u.PriceChangeNotifiedAt == null
                    || u.PriceChangeNotifiedAt < DateTimeOffset.UtcNow.AddDays(-30)))
            .ToListAsync(ct);

        if (affectedUsers.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Price change detected ({OldPrices} -> {NewPrice}). Notifying {Count} users.",
            string.Join(", ", affectedUsers.Select(u => u.AutoRechargePricePer100).Distinct()),
            currentPrice, affectedUsers.Count);

        var baseUrl = appOptions.BaseUrl;

        foreach (var user in affectedUsers)
        {
            var cancelUrl = $"{baseUrl}/Billing/AutoRecharge/Cancel?token={user.AutoRechargeCancelToken}";
            var oldAmount = GetFormattedAmount(user.AutoRechargePricePer100, user.AutoRechargeQuantity, billingOptions);
            var newAmount = pricing.GetPack(user.AutoRechargeQuantity) is { } pack
                ? pack.Amount.ToString("0.##")
                : GetFormattedAmount(currentPrice, user.AutoRechargeQuantity, billingOptions);
            // Background job: the language of the user's last sign-in, not the thread's.
            var locale = user.EmailLocale;
            var variables = EmailTemplateVariables.PriceChangeNotice(
                daysNotice: "30",
                quantity: user.AutoRechargeQuantity.ToString(),
                oldPrice: oldAmount,
                newPrice: newAmount,
                currency: pricing.Currency,
                cancelUrl: cancelUrl,
                billingUrl: $"{baseUrl}/Billing");

            var emailOptions = new EmailSendOptions(EmailTemplateId.PriceChangeNotice, locale, user.Id);
            // Email module On: queued with PriceChangeNotifiedAt in the SaveChangesAsync below.
            if (email.TryEnqueue(EmailTemplateId.PriceChangeNotice, user.Email, locale, variables, emailOptions))
            {
                user.PriceChangeNotifiedAt = DateTimeOffset.UtcNow;
                continue;
            }

            try
            {
                await email.SendAsync(EmailTemplateId.PriceChangeNotice, user.Email, locale, variables, emailOptions);

                user.PriceChangeNotifiedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Price change notification sent to user {UserId} ({Email})", user.Id, user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send price change notification to {Email}", user.Email);
            }
        }

        await dbContext.SaveChangesAsync(ct);

        // After 30 days from notification, update the stored price for users who haven't cancelled
        var usersToUpdatePrice = await dbContext.Users
            .Where(u => u.AutoRechargeEnabled
                && u.AutoRechargePricePer100 != currentPrice
                && u.PriceChangeNotifiedAt != null
                && u.PriceChangeNotifiedAt <= DateTimeOffset.UtcNow.AddDays(-30))
            .ToListAsync(ct);

        foreach (var user in usersToUpdatePrice)
        {
            user.AutoRechargePricePer100 = currentPrice;
            user.PriceChangeNotifiedAt = null;
            _logger.LogInformation("Updated stored auto-recharge price for user {UserId} to {Price}", user.Id, currentPrice);
        }

        if (usersToUpdatePrice.Count > 0)
        {
            await dbContext.SaveChangesAsync(ct);
        }
    }

    private static string GetFormattedAmount(decimal pricePer100, int quantity, BillingOptions options) =>
        ConfigCreditPricingProvider.CalculateAmount(quantity, pricePer100, options).ToString("0.##");
}
