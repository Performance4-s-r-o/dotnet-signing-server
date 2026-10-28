using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>The permanent once-only grant key (#79), on InMemory; the SQL path is in <see cref="BillingPostgresTests"/>.</summary>
public class CreditGrantsTests
{
    private readonly string _dbName = "grants-" + Guid.NewGuid();

    private ApplicationDbContext Db() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<Guid> NewUserAsync()
    {
        var user = TestHelpers.CreateTestUser($"{Guid.NewGuid():N}@example.com");
        user.CreditsRemaining = 10;
        await using var db = Db();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<int> CreditsAsync(Guid userId)
    {
        await using var db = Db();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.CreditsRemaining).SingleAsync();
    }

    private async Task<bool> GrantAsync(Guid userId, string key, string? paymentIntent, Func<Task>? alongside = null)
    {
        await using var db = Db();
        return await CreditGrants.TryGrantAsync(db, key, userId, 300, "test",
            alongside ?? (() => Task.CompletedTask), default, paymentIntent);
    }

    [Fact]
    public async Task APayment_IsGrantedOnce_AndTheKeyIsKept()
    {
        var userId = await NewUserAsync();

        Assert.True(await GrantAsync(userId, "cs_1", "pi_1"));
        Assert.False(await GrantAsync(userId, "cs_1", "pi_1"));

        Assert.Equal(310, await CreditsAsync(userId));
        await using var db = Db();
        var grant = await db.CreditGrants.AsNoTracking().SingleAsync();
        Assert.Equal(("cs_1", "pi_1", userId, 300, "test"), (grant.GrantKey, grant.PaymentIntentId, grant.UserId, grant.Credits, grant.Source));
        Assert.Empty(db.WebhookEvents); // the grant key lives in CreditGrants alone
    }

    [Fact]
    public async Task ThePaymentIntent_IsUniqueAcrossKeys()
    {
        var userId = await NewUserAsync();

        Assert.True(await GrantAsync(userId, "cs_1", "pi_shared"));
        Assert.False(await GrantAsync(userId, "auto_recharge_pi_shared", "pi_shared"));
        Assert.True(await GrantAsync(userId, "cs_2", null)); // unknown payment intent: the key alone decides

        Assert.Equal(610, await CreditsAsync(userId));
    }

    [Fact]
    public async Task AFailedGrant_LeavesNothing_SoTheRetryGrants()
    {
        var userId = await NewUserAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => GrantAsync(userId, "cs_1", "pi_1", () => throw new TimeoutException("db")));
        Assert.Equal(10, await CreditsAsync(userId));
        await using (var db = Db()) Assert.False(await CreditGrants.IsGrantedAsync(db, "cs_1", default));

        Assert.True(await GrantAsync(userId, "cs_1", "pi_1"));
        Assert.Equal(310, await CreditsAsync(userId));
    }
}
