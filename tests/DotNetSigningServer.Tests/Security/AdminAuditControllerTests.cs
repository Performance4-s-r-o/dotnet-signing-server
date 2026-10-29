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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>#80: every admin action on an account leaves actor, target and change behind.</summary>
public class AdminAuditControllerTests
{
    private static readonly Guid ActorId = Guid.NewGuid();

    private static AdminController Controller(DotNetSigningServer.Data.ApplicationDbContext db)
    {
        var controller = new AdminController(
            db,
            Mock.Of<IAutoRechargeService>(),
            Mock.Of<IPaymentGateway>(),
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
    public async Task ToggleTrustedCaller_IsAudited_BothWays()
    {
        var (db, user) = await SeedAsync();
        await Controller(db).ToggleTrustedCaller(user.Id);
        Assert.True((await db.Users.SingleAsync(u => u.Id == user.Id)).TrustedCaller);
        await Controller(db).ToggleTrustedCaller(user.Id);
        Assert.False((await db.Users.SingleAsync(u => u.Id == user.Id)).TrustedCaller);

        var entries = await db.AdminAuditEntries.ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(ActorId, e.ActorUserId);
            Assert.Equal(user.Id, e.TargetUserId);
            Assert.Equal(AdminAudit.Actions.TrustedCaller, e.Action);
        });
        Assert.Single(entries, e => e.Change.Contains("\"to\":true"));
        Assert.Single(entries, e => e.Change.Contains("\"to\":false"));
    }

    [Fact]
    public async Task ToggleTrustedCaller_RefusesTheAdminsOwnAccount()
    {
        var db = TestHelpers.CreateInMemoryDbContext();
        db.Users.Add(new User { Id = ActorId, Email = "admin@example.cz", IsAdmin = true });
        await db.SaveChangesAsync();

        await Controller(db).ToggleTrustedCaller(ActorId);

        Assert.False((await db.Users.SingleAsync()).TrustedCaller);
        Assert.Empty(await db.AdminAuditEntries.ToListAsync());
    }

    [Fact]
    public void ToggleTrustedCaller_IsAdminOnly()
    {
        var policy = typeof(AdminController).GetCustomAttributes(typeof(CookieAuthorizeAttribute), true)
            .Cast<CookieAuthorizeAttribute>().Single().Policy;
        Assert.Equal("AdminOnly", policy);
        var action = typeof(AdminController).GetMethod(nameof(AdminController.ToggleTrustedCaller))!;
        Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        Assert.NotEmpty(action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Fact]
    public void TrustedCaller_IsOffByDefault()
    {
        Assert.False(new User().TrustedCaller);
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
}
