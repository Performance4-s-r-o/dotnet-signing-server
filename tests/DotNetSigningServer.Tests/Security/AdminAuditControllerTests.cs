using System.Security.Claims;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>#80: every admin action on an account leaves actor, target and change behind.</summary>
public class AdminAuditControllerTests
{
    private static readonly Guid ActorId = Guid.NewGuid();

    private static AdminController Controller(DotNetSigningServer.Data.ApplicationDbContext db, IPaymentGateway? payments = null, IAutoRechargeService? autoRecharge = null)
    {
        var controller = new AdminController(
            db,
            autoRecharge ?? Mock.Of<IAutoRechargeService>(),
            payments ?? Mock.Of<IPaymentGateway>(),
            NullLogger<AdminController>.Instance,
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()),
            TestHelpers.WrapOptions(new P4BackofficeProductOptions()),
            new OutboxSignal(),
            TimeProvider.System);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()),
                new Claim(ClaimTypes.Name, "admin@example.cz"),
                new Claim(ClaimTypes.Role, "Admin"),
            }, "Cookies")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static async Task<(DotNetSigningServer.Data.ApplicationDbContext, User)> SeedAsync()
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        var user = new User { Email = "target@example.cz", CreditsRemaining = 7 };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    [Fact]
    public async Task ToggleSealAllowed_IsAudited()
    {
        var (db, user) = await SeedAsync();
        await Controller(db).ToggleSealAllowed(user.Id);

        var entry = await db.AdminAuditEntries.SingleAsync();
        Assert.Equal(ActorId, entry.ActorUserId);
        Assert.Equal("admin@example.cz", entry.ActorEmail);
        Assert.Equal(user.Id, entry.TargetUserId);
        Assert.Equal(AdminAudit.Actions.SealAllowed, entry.Action);
        Assert.Contains("\"from\":false", entry.Change);
        Assert.Contains("\"to\":true", entry.Change);
    }

    [Fact]
    public async Task Enterprise_and_seal_cannot_be_set_on_a_provisioned_account()
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        var parent = new User { Email = "parent@service.invalid", IsServiceAccount = true, IsEnterprise = true, SealAllowed = true };
        var child = new User { Email = "child@service.invalid", IsServiceAccount = true, ParentUserId = parent.Id, ExternalId = "t-1" };
        db.Users.AddRange(parent, child);
        await db.SaveChangesAsync();

        var enterprise = Controller(db);
        await enterprise.ToggleEnterprise(child.Id);
        var seal = Controller(db);
        await seal.ToggleSealAllowed(child.Id);

        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == child.Id);
        Assert.False(stored.IsEnterprise);
        Assert.False(stored.SealAllowed);
        Assert.Equal("AdminInheritedFromParent", enterprise.TempData["Error"]);
        Assert.Equal("AdminInheritedFromParent", seal.TempData["Error"]);
        Assert.Empty(await db.AdminAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task ToggleEnterprise_IsAudited_BothWays()
    {
        var (db, user) = await SeedAsync();
        await Controller(db).ToggleEnterprise(user.Id);
        await Controller(db).ToggleEnterprise(user.Id);

        var entries = await db.AdminAuditEntries.OrderBy(a => a.CreatedAt).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(AdminAudit.Actions.Enterprise, e.Action));
        Assert.Single(entries, e => e.Change.Contains("\"to\":true"));
        Assert.Single(entries, e => e.Change.Contains("\"to\":false"));
    }

    [Fact]
    public async Task SetConcurrencyQueueTimeout_IsAudited()
    {
        var (db, user) = await SeedAsync();
        await Controller(db).SetConcurrencyQueueTimeout(user.Id, 30);

        var entry = await db.AdminAuditEntries.SingleAsync();
        Assert.Equal(AdminAudit.Actions.ConcurrencyQueueTimeout, entry.Action);
        Assert.Contains("\"to\":30", entry.Change);
    }

    [Fact]
    public async Task ToggleEnterprise_WhenDetachingTheCardFails_ChangesNothing()
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        var user = new User { Email = "t@example.cz", StripeCustomerId = "cus_1", AutoRechargeEnabled = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var payments = new Mock<IPaymentGateway>();
        payments.Setup(p => p.DetachPaymentMethodsAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("down"));
        var autoRecharge = new Mock<IAutoRechargeService>();
        var controller = Controller(db, payments.Object, autoRecharge.Object);

        await controller.ToggleEnterprise(user.Id);

        Assert.False((await db.Users.SingleAsync()).IsEnterprise);
        autoRecharge.Verify(a => a.DisableAsync(It.IsAny<User>()), Times.Never);
        Assert.Empty(await db.AdminAuditEntries.ToListAsync());
        Assert.Equal("EnterpriseDetachFailed", controller.TempData["Error"]);

        // The retry, with the service back, enables it.
        payments.Setup(p => p.DetachPaymentMethodsAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await controller.ToggleEnterprise(user.Id);
        Assert.True((await db.Users.SingleAsync()).IsEnterprise);
    }
}
