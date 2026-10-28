using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Billing;

/// <summary>
/// #82: a checkout naming a user in its metadata acts on that user only when its customer
/// is theirs, and a user is never linked to a customer another user holds.
/// </summary>
[Trait("Category", "Db")]
public class StripeCustomerOwnershipTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;
    private readonly Mock<IAutoRechargeService> _autoRecharge = new();

    public StripeCustomerOwnershipTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private async Task<User> NewUserAsync(string? stripeCustomerId)
    {
        var user = TestHelpers.CreateTestUser($"{Guid.NewGuid():N}@example.com");
        user.CreditsRemaining = 0;
        user.StripeCustomerId = stripeCustomerId;
        await using var db = _pg.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<User> ReloadAsync(Guid userId)
    {
        await using var db = _pg.CreateContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<WebhookEvent> RefusalAsync(string type, string stripeObjectId)
    {
        await using var db = _pg.CreateContext();
        var row = await db.WebhookEvents.AsNoTracking()
            .SingleOrDefaultAsync(w => w.EventId == BillingRefusals.EventIdFor(type, stripeObjectId));
        Assert.NotNull(row);
        Assert.Equal(type, row.EventType);
        Assert.False(string.IsNullOrEmpty(row.Error));
        return row;
    }

    // --- Confirm pages (redirect from Stripe) ---------------------------------------------

    private BillingController Billing(ApplicationDbContext db, User user, Stripe.Checkout.Session session)
    {
        var checkout = new Mock<IStripeCheckoutService>();
        checkout.Setup(c => c.GetSessionAsync(session.Id)).ReturnsAsync(session);
        var pricing = new Mock<ICreditPricingProvider>();
        pricing.SetupGet(p => p.PricePer100).Returns(5m);
        var controller = new BillingController(
            db,
            new BillingService(TestHelpers.WrapOptions(new BillingOptions())),
            pricing.Object,
            new StripePaymentGateway(checkout.Object, NullLogger<StripePaymentGateway>.Instance),
            _autoRecharge.Object,
            NullLogger<BillingController>.Instance,
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        controller.Url = Mock.Of<IUrlHelper>();
        return controller;
    }

    private static Stripe.Checkout.Session SetupSession(Guid userId, string? customerId) => new()
    {
        Id = Unique("cs"),
        Mode = "setup",
        Status = "complete",
        CustomerId = customerId,
        Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString(), [BillingCustomerRef.ProductMarkerKey] = BillingCustomerRef.ProductMarker, ["autoRechargeQuantity"] = "300" },
    };

    private static Stripe.Checkout.Session PaidSession(Guid userId, string? customerId) => new()
    {
        Id = Unique("cs"),
        Mode = "payment",
        Status = "complete",
        PaymentStatus = "paid",
        CustomerId = customerId,
        Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString(), [BillingCustomerRef.ProductMarkerKey] = BillingCustomerRef.ProductMarker, ["documents"] = "300" },
    };

    [Fact]
    public async Task ConfirmAutoRechargeSetup_WithAnotherUsersCustomer_DoesNotLinkIt()
    {
        var holder = await NewUserAsync(Unique("cus_held"));
        var user = await NewUserAsync(stripeCustomerId: null);
        var session = SetupSession(user.Id, holder.StripeCustomerId);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmAutoRechargeSetup(session.Id);
        }

        Assert.Null((await ReloadAsync(user.Id)).StripeCustomerId);
        _autoRecharge.Verify(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAutoRechargeSetup_OfAnotherCustomer_DoesNotEnableAutoRecharge()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var session = SetupSession(user.Id, Unique("cus_other"));

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmAutoRechargeSetup(session.Id);
        }

        Assert.Equal(user.StripeCustomerId, (await ReloadAsync(user.Id)).StripeCustomerId);
        _autoRecharge.Verify(a => a.EnableAsync(It.IsAny<User>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmCheckout_OfAnotherCustomer_GrantsNothing_AndOwnCustomerGrants()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var foreign = PaidSession(user.Id, Unique("cus_other"));
        var own = PaidSession(user.Id, user.StripeCustomerId);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, foreign).ConfirmCheckout(foreign.Id);
        }
        Assert.Equal(0, (await ReloadAsync(user.Id)).CreditsRemaining);
        await RefusalAsync(BillingRefusals.CheckoutRefused, foreign.Id);

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, own).ConfirmCheckout(own.Id);
        }
        Assert.Equal(300, (await ReloadAsync(user.Id)).CreditsRemaining);
    }

    [Fact]
    public async Task ConfirmCheckout_WithoutUserIdMetadata_GrantsNothing()
    {
        var user = await NewUserAsync(Unique("cus_own"));
        var session = PaidSession(user.Id, user.StripeCustomerId);
        session.Metadata.Remove("userId");

        await using (var db = _pg.CreateContext())
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            await Billing(db, dbUser, session).ConfirmCheckout(session.Id);
        }

        Assert.Equal(0, (await ReloadAsync(user.Id)).CreditsRemaining);
    }

    /// <summary>
    /// Fires once, right after the "does another user hold this customer?" query has run: the
    /// race the check alone cannot close.
    /// </summary>
    private sealed class AfterHolderCheck(Func<Task> act) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("StripeCustomerId") && command.CommandText.Contains("EXISTS")
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await act();
            }
            return result;
        }
    }

    [Fact]
    public async Task Bind_LosingTheRaceToAnotherUser_IsARefusal_NotAnError()
    {
        var other = await NewUserAsync(stripeCustomerId: null);
        var user = await NewUserAsync(stripeCustomerId: null);
        var customer = Unique("cus_raced");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        _pg.Configure(options);
        options.AddInterceptors(new AfterHolderCheck(async () =>
        {
            await using var otherDb = _pg.CreateContext();
            var o = await otherDb.Users.SingleAsync(u => u.Id == other.Id);
            o.StripeCustomerId = customer;
            await otherDb.SaveChangesAsync();
        }));
        await using (var db = new ApplicationDbContext(options.Options))
        {
            var dbUser = await db.Users.SingleAsync(u => u.Id == user.Id);
            var bound = await StripeCustomerBinding.TryBindAsync(db, dbUser, customer, NullLogger.Instance);

            Assert.False(bound);
            Assert.Null(dbUser.StripeCustomerId);
            await db.SaveChangesAsync(); // the context stays usable
        }

        Assert.Null((await ReloadAsync(user.Id)).StripeCustomerId);
        Assert.Equal(customer, (await ReloadAsync(other.Id)).StripeCustomerId);
    }
}
