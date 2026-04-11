using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Background service that checks once per day whether the configured PricePer100
/// has changed compared to users' stored AutoRechargePricePer100.
/// If a change is detected, users are notified 30 days in advance
/// and given the option to cancel auto-recharge before the new price takes effect.
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
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var appOptions = scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>().Value;

        var currentPrice = billingOptions.PricePer100;

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

        var baseUrl = appOptions.FqdnServerName?.TrimEnd('/') ?? "https://app.p4pdf.com";

        foreach (var user in affectedUsers)
        {
            if (!user.EmailNotificationsEnabled)
            {
                // Can't notify, but mark as notified so the price updates after 30 days
                user.PriceChangeNotifiedAt = DateTimeOffset.UtcNow;
                continue;
            }

            var cancelUrl = $"{baseUrl}/Billing/AutoRecharge/Cancel?token={user.AutoRechargeCancelToken}";
            var oldAmount = GetFormattedAmount(user.AutoRechargePricePer100, user.AutoRechargeQuantity, billingOptions);
            var newAmount = GetFormattedAmount(currentPrice, user.AutoRechargeQuantity, billingOptions);

            var body = $@"
<div style=""font-family:sans-serif;max-width:600px;margin:0 auto"">
    <h2>Auto-recharge price change notice</h2>
    <p>The price for document signing credits will change in <strong>30 days</strong>.</p>
    <table style=""border-collapse:collapse;margin:16px 0"">
        <tr>
            <td style=""padding:8px 16px;border:1px solid #ddd"">Auto-recharge quantity</td>
            <td style=""padding:8px 16px;border:1px solid #ddd""><strong>{user.AutoRechargeQuantity} credits</strong></td>
        </tr>
        <tr>
            <td style=""padding:8px 16px;border:1px solid #ddd"">Current price</td>
            <td style=""padding:8px 16px;border:1px solid #ddd"">{oldAmount} {billingOptions.Currency}</td>
        </tr>
        <tr>
            <td style=""padding:8px 16px;border:1px solid #ddd"">New price (effective in 30 days)</td>
            <td style=""padding:8px 16px;border:1px solid #ddd""><strong>{newAmount} {billingOptions.Currency}</strong></td>
        </tr>
    </table>
    <p>If you do not wish to continue with auto-recharge at the new price, you can cancel it before the change takes effect:</p>
    <p><a href=""{cancelUrl}"" style=""display:inline-block;padding:12px 24px;background:#dc3545;color:#fff;text-decoration:none;border-radius:4px"">Cancel auto-recharge</a></p>
    <p>You can also manage auto-recharge from your <a href=""{baseUrl}/Billing"">billing page</a>.</p>
    <p style=""color:#666;font-size:13px"">If you take no action, your auto-recharge will continue at the new price after 30 days.</p>
</div>";

            try
            {
                await emailSender.SendAsync(
                    user.Email,
                    "Auto-recharge price change – 30 day notice",
                    body,
                    $"{baseUrl}/Account/Settings",
                    isCritical: true);

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

    private static string GetFormattedAmount(decimal pricePer100, int quantity, BillingOptions options)
    {
        int units = (int)Math.Ceiling(quantity / 100m);
        var baseAmount = units * pricePer100;

        decimal discount = 0m;
        if (quantity >= 1000) discount = options.Discount1000;
        else if (quantity >= 500) discount = options.Discount500;
        else if (quantity >= 300) discount = options.Discount300;

        if (discount > 0)
            baseAmount -= baseAmount * discount;

        return Math.Round(baseAmount, 2, MidpointRounding.AwayFromZero).ToString("0.##");
    }
}
