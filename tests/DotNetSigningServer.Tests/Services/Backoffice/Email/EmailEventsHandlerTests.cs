using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class EmailEventsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationDbContext Db(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task<User> AddUserAsync(string dbName, string email = "jan@example.com")
    {
        await using var db = Db(dbName);
        var user = new User { Email = email };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task HandleAsync(string dbName, string type, string dataJson)
    {
        await using var db = Db(dbName);
        var handler = new EmailEventsHandler(db, new ManualTimeProvider(Now), NullLogger<EmailEventsHandler>.Instance);
        var data = JsonDocument.Parse(dataJson).RootElement.Clone();
        await handler.HandleAsync(new BackofficeEvent("msg_1", type, data, "webhook", Now, 1), CancellationToken.None);
    }

    private static async Task<DateTimeOffset?> BouncedAtAsync(string dbName, Guid userId)
    {
        await using var db = Db(dbName);
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).EmailBouncedAt;
    }

    [Theory]
    [InlineData(BackofficeEventTypes.EmailBounced)]
    [InlineData(BackofficeEventTypes.EmailComplained)]
    public async Task BouncedOrComplained_SetsEmailBouncedAt_ByTheUserIdTag(string type)
    {
        var dbName = Guid.NewGuid().ToString();
        var user = await AddUserAsync(dbName, "someone-else@example.com");

        await HandleAsync(dbName, type,
            $$$"""{"email_id":"e1","to":"old@example.com","status":"bounced","tags":{"template":"two_factor_code","user_id":"{{{user.Id}}}"},"occurred_at":"2026-09-28T11:00:00Z"}""");

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 11, 0, 0, TimeSpan.Zero), await BouncedAtAsync(dbName, user.Id));
    }

    [Fact]
    public async Task WithoutUserIdTag_FindsTheUserByAddress()
    {
        var dbName = Guid.NewGuid().ToString();
        var user = await AddUserAsync(dbName);

        await HandleAsync(dbName, BackofficeEventTypes.EmailBounced,
            """{"email_id":"e1","to":"jan@example.com","status":"bounced","tags":{}}""");

        Assert.Equal(Now, await BouncedAtAsync(dbName, user.Id));
    }

    [Fact]
    public async Task OlderEvent_DoesNotMoveTheTimeBack()
    {
        var dbName = Guid.NewGuid().ToString();
        var user = await AddUserAsync(dbName);
        await HandleAsync(dbName, BackofficeEventTypes.EmailBounced,
            """{"to":"jan@example.com","tags":{},"occurred_at":"2026-09-28T11:00:00Z"}""");

        await HandleAsync(dbName, BackofficeEventTypes.EmailBounced,
            """{"to":"jan@example.com","tags":{},"occurred_at":"2026-09-01T11:00:00Z"}""");

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 11, 0, 0, TimeSpan.Zero), await BouncedAtAsync(dbName, user.Id));
    }

    [Fact]
    public async Task Failed_OnlyLogs()
    {
        var dbName = Guid.NewGuid().ToString();
        var user = await AddUserAsync(dbName);

        await HandleAsync(dbName, BackofficeEventTypes.EmailFailed,
            $$$"""{"email_id":"e1","to":"jan@example.com","status":"failed","tags":{"user_id":"{{{user.Id}}}"}}""");

        Assert.Null(await BouncedAtAsync(dbName, user.Id));
    }

    [Fact]
    public async Task UnknownUser_IsIgnored()
    {
        var dbName = Guid.NewGuid().ToString();
        await AddUserAsync(dbName);

        await HandleAsync(dbName, BackofficeEventTypes.EmailBounced,
            $$$"""{"to":"nobody@example.com","tags":{"user_id":"{{{Guid.NewGuid()}}}"}}""");

        await using var db = Db(dbName);
        Assert.All(await db.Users.ToListAsync(), u => Assert.Null(u.EmailBouncedAt));
    }

    [Fact]
    public void HandlesTheThreeEmailEvents()
    {
        var handler = new EmailEventsHandler(Db("x"), TimeProvider.System, NullLogger<EmailEventsHandler>.Instance);
        Assert.Equal(
            new[] { BackofficeEventTypes.EmailBounced, BackofficeEventTypes.EmailComplained, BackofficeEventTypes.EmailFailed },
            handler.Types);
    }
}
