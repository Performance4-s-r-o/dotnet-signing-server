using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>Auto-recharge on a host without the billing service (development): nothing is charged, nothing floods the log.</summary>
public class AutoRechargeWithoutBillingTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task WithoutTheBillingService_NothingIsCharged_AndTheNextDebitIsHeldByTheCooldown()
    {
        var user = TestHelpers.CreateTestUser();
        user.AutoRechargeEnabled = true;
        user.AutoRechargeQuantity = 300;
        user.CreditsRemaining = 0;
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        var service = new AutoRechargeService(_db, new DotNetSigningServer.Tests.Services.Pricing.FakeTemplatedEmailSender(),
            NullLogger<AutoRechargeService>.Instance, Microsoft.Extensions.Options.Options.Create(new DotNetSigningServer.Options.AppOptions()),
            Mock.Of<ICreditPricingProvider>());

        var first = await service.TryAutoRechargeAsync(user.Id);
        var second = await service.TryAutoRechargeAsync(user.Id);

        Assert.Equal("Payments not available", first.Error);
        Assert.Contains("Cooldown", second.Error);
        Assert.Equal(0, user.CreditsRemaining);
    }
}
