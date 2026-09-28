using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class BackofficeOutboxTests
{
    private static async Task<IReadOnlyList<Guid>> SignalsAsync(OutboxSignal signal) =>
        await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

    [Fact]
    public async Task EnqueueAndSaveChanges_SignalsTheDispatcher()
    {
        using var host = new OutboxTestHost();
        using var scope = host.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var id = outbox.Enqueue("test", new { email = "a@example.com" });
        Assert.Empty(await SignalsAsync(host.Signal));

        await db.SaveChangesAsync();

        Assert.Equal(new[] { id }, await SignalsAsync(host.Signal));
        var item = await host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal(0, item.Attempts);
        Assert.Equal(host.Time.Now, item.NextAttemptAt);
        Assert.Equal(host.Time.Now, item.CreatedAt);
    }

    [Fact]
    public async Task EnqueueWithoutSaveChanges_WritesAndSendsNothing()
    {
        using var host = new OutboxTestHost();
        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>().Enqueue("test", new { x = 1 });
            // Scope ends without SaveChanges — e.g. the request failed validation.
        }

        Assert.Empty(await SignalsAsync(host.Signal));
        Assert.Equal(0, await host.WithDbAsync(db => db.BackofficeOutboxItems.CountAsync()));
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));
        Assert.Empty(host.Service.Requests);
    }

    [Fact]
    public async Task EnqueuedItem_IsSavedTogetherWithTheDomainChange()
    {
        using var host = new OutboxTestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();

        db.WebhookEvents.Add(new WebhookEvent { EventId = "evt_1", PayloadJson = "{}" });
        var id = outbox.Enqueue("test", new { x = 1 });
        var written = await db.SaveChangesAsync();

        Assert.Equal(2, written);
        Assert.NotNull(await host.ItemAsync(id));
    }

    [Fact]
    public async Task Payload_IsStoredEncryptedAsSnakeCaseJson()
    {
        using var host = new OutboxTestHost();
        const string otp = "493817";
        const string resetLink = "https://app.example.com/reset?token=very-secret-token";

        var id = await host.EnqueueAsync(new { Code = otp, ResetUrl = resetLink });

        var item = await host.ItemAsync(id);
        Assert.NotNull(item.PayloadProtected);
        Assert.DoesNotContain(otp, item.PayloadProtected);
        Assert.DoesNotContain("very-secret-token", item.PayloadProtected);

        var protector = host.Services.GetRequiredService<OutboxPayloadProtector>();
        var json = protector.Unprotect(item.PayloadProtected!);
        Assert.Equal($$"""{"code":"{{otp}}","reset_url":"{{resetLink}}"}""", json);
    }

    [Fact]
    public async Task SaveChangesTwice_SignalsEachItemOnce()
    {
        using var host = new OutboxTestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();

        var first = outbox.Enqueue("test", new { n = 1 });
        await db.SaveChangesAsync();
        var second = outbox.Enqueue("test", new { n = 2 }, critical: true);
        await db.SaveChangesAsync();
        await db.SaveChangesAsync();

        Assert.Equal(new[] { first, second }, await SignalsAsync(host.Signal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-kind-that-is-far-too-long-for-the-kind-column")]
    public void Enqueue_RejectsInvalidKinds(string kind)
    {
        using var host = new OutboxTestHost();
        using var scope = host.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();

        Assert.ThrowsAny<ArgumentException>(() => outbox.Enqueue(kind, new { }));
    }

    [Fact]
    public async Task Signal_IsLossyButNeverBlocks()
    {
        var signal = new OutboxSignal();
        for (var i = 0; i < OutboxSignal.Capacity + 10; i++) signal.Notify(Guid.NewGuid());

        var drained = await signal.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(OutboxSignal.Capacity, drained.Count);
        Assert.Empty(await signal.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }
}
