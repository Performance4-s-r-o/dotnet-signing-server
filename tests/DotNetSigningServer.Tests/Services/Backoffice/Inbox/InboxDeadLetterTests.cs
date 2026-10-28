using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

/// <summary>Dead letters, replay and the cross-instance claim of the inbox (#79), on InMemory.</summary>
public class InboxDeadLetterTests
{
    private static async Task FailUntilDeadAsync(InboxTestHost host)
    {
        for (var i = 0; i < BackofficeInboxProcessor.MaxAttempts + 3; i++)
        {
            await host.Processor.ProcessDueAsync(CancellationToken.None);
            host.Time.Advance(TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public async Task OutOfAttempts_TheItemIsDead_AndAnAlertIsLogged()
    {
        var logs = new CapturingLoggerProvider();
        var handler = new RecordingEventHandler(BackofficeEventTypes.EmailBounced) { Fail = _ => new Exception("down") };
        using var host = new InboxTestHost(services: s =>
        {
            s.AddSingleton<IBackofficeEventHandler>(handler);
            s.AddSingleton<ILoggerProvider>(logs);
        });
        await host.AddAsync("msg_1", BackofficeEventTypes.EmailBounced);

        await FailUntilDeadAsync(host);

        var item = Assert.Single(await host.ItemsAsync());
        Assert.NotNull(item.DeadAt);
        Assert.True(item.IsDead);
        Assert.Null(item.NextAttemptAt);
        Assert.Equal(BackofficeInboxProcessor.MaxAttempts, handler.Received.Count);
        var alert = Assert.Single(logs.Errors, m => m.StartsWith(BackofficeInboxProcessor.AlertPrefix, StringComparison.Ordinal));
        Assert.Contains("msg_1", alert);
        Assert.Equal(1, await host.WithDbAsync(db => BackofficeInboxProcessor.CountDeadAsync(db)));

        // Kept by the cleanup, however old.
        var later = host.Time.Now + BackofficeInboxProcessor.Retention + TimeSpan.FromDays(10);
        Assert.Equal(0, await host.WithDbAsync(db => BackofficeInboxProcessor.DeleteProcessedAsync(db, later)));
    }

    [Fact]
    public void DeadBillingCheckout_AlertSaysCreditsAreMissing()
    {
        Assert.Contains("credits", BackofficeInboxProcessor.Consequence(BackofficeEventTypes.BillingCheckoutCompleted));
        Assert.Equal("", BackofficeInboxProcessor.Consequence(BackofficeEventTypes.EmailBounced));
    }

    [Fact]
    public async Task Replay_PutsDeadItemsBackWithAFreshRound()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.EmailBounced) { Fail = _ => new Exception("down") };
        using var host = new InboxTestHost(services: s => s.AddSingleton<IBackofficeEventHandler>(handler));
        await host.AddAsync("msg_1", BackofficeEventTypes.EmailBounced);
        await host.AddAsync("msg_2", BackofficeEventTypes.EmailBounced);
        await FailUntilDeadAsync(host);
        handler.Fail = _ => null;

        // Dead items are never retried on their own.
        Assert.Equal(0, await host.Processor.ProcessDueAsync(CancellationToken.None));

        var replayed = await host.WithDbAsync(db => BackofficeInboxProcessor.ReplayDeadAsync(db, host.Time.Now, "msg_2"));
        Assert.Single(replayed);
        Assert.Equal(1, await host.Processor.ProcessDueAsync(CancellationToken.None));

        var items = await host.ItemsAsync();
        var two = items.Single(i => i.WebhookId == "msg_2");
        Assert.NotNull(two.ProcessedAt);
        Assert.Null(two.DeadAt);
        Assert.Equal(1, two.Attempts);
        Assert.True(items.Single(i => i.WebhookId == "msg_1").IsDead);

        Assert.Single(await host.WithDbAsync(db => BackofficeInboxProcessor.ReplayDeadAsync(db, host.Time.Now)));
        Assert.Empty(await host.WithDbAsync(db => BackofficeInboxProcessor.ReplayDeadAsync(db, host.Time.Now)));
    }

    [Fact]
    public async Task ClaimedItem_IsNotRunByAnotherInstance_UntilTheClaimExpires()
    {
        var handler = new RecordingEventHandler(BackofficeEventTypes.PriceScheduled);
        using var host = new InboxTestHost(services: s => s.AddSingleton<IBackofficeEventHandler>(handler));
        await host.AddAsync("msg_1", BackofficeEventTypes.PriceScheduled);

        // Another instance claimed it (and is still running it).
        var claimed = await host.WithDbAsync(db => BackofficeInboxProcessor.ClaimDueAsync(db, host.Time.Now, default));
        Assert.Single(claimed);
        Assert.Empty(await host.WithDbAsync(db => BackofficeInboxProcessor.ClaimDueAsync(db, host.Time.Now, default)));

        Assert.Equal(0, await host.Processor.ProcessDueAsync(CancellationToken.None));
        Assert.Empty(handler.Received);

        // The other instance died: once the claim has expired, this one runs it.
        host.Time.Advance(BackofficeInboxProcessor.ClaimDuration);
        Assert.Equal(1, await host.Processor.ProcessDueAsync(CancellationToken.None));
        Assert.Single(handler.Received);
        Assert.NotNull(Assert.Single(await host.ItemsAsync()).ProcessedAt);
    }

    /// <summary>The real billing handler behind a switch that makes it fail.</summary>
    private sealed class FlakyBillingHandler : IBackofficeEventHandler
    {
        private readonly ApplicationDbContext _db;
        private readonly IServiceProvider _services;
        private readonly Switch _switch;

        public FlakyBillingHandler(ApplicationDbContext db, IServiceProvider services, Switch @switch)
        {
            _db = db;
            _services = services;
            _switch = @switch;
        }

        public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.BillingCheckoutCompleted];

        public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
        {
            if (_switch.Failing) throw new InvalidOperationException("database timeout");
            return new BillingEventsHandler(_db, _services, NullLogger<BillingEventsHandler>.Instance)
                .HandleAsync(evt, cancellationToken);
        }
    }

    private sealed class Switch
    {
        public bool Failing { get; set; } = true;
    }

    private static string Checkout(Guid userId, string sessionId) => $$$"""
        {"customer_ref":"{{{BillingCustomerRef.For(userId)}}}","kind":"payment","payment_status":"paid","stripe_session_id":"{{{sessionId}}}",
         "amount_total":1500,"currency":"eur","stripe_payment_intent_id":"pi_{{{sessionId}}}","stripe_customer_id":"cus_1",
         "metadata":{"product":"dotnet-signing-server","userId":"{{{userId}}}","documents":"300","autoRecharge":"False"}}
        """;

    [Fact]
    public async Task DeadCheckoutCompleted_ReplayedGrantsTheCreditsExactlyOnce()
    {
        var failing = new Switch();
        using var host = new InboxTestHost(services: s =>
        {
            s.AddSingleton(failing);
            s.AddScoped<IBackofficeEventHandler, FlakyBillingHandler>();
        });
        var user = TestHelpers.CreateTestUser("buyer@example.com");
        user.CreditsRemaining = 0;
        await host.WithDbAsync(db =>
        {
            db.Users.Add(user);
            return db.SaveChangesAsync();
        });
        await host.AddAsync("msg_checkout", BackofficeEventTypes.BillingCheckoutCompleted, Checkout(user.Id, "cs_paid"));

        await FailUntilDeadAsync(host);
        Assert.True(Assert.Single(await host.ItemsAsync()).IsDead);
        Assert.Equal(0, await CreditsAsync(host, user.Id));

        // The cause is fixed; an admin replays the dead event.
        failing.Failing = false;
        await host.WithDbAsync(db => BackofficeInboxProcessor.ReplayDeadAsync(db, host.Time.Now));
        await host.Processor.ProcessDueAsync(CancellationToken.None);
        Assert.Equal(300, await CreditsAsync(host, user.Id));

        // The same purchase again — the polled copy under another id, or a second replay — grants nothing.
        await host.AddAsync("msg_checkout_poll", BackofficeEventTypes.BillingCheckoutCompleted, Checkout(user.Id, "cs_paid"), BackofficeInboxSource.Poll);
        await host.Processor.ProcessDueAsync(CancellationToken.None);
        Assert.Empty(await host.WithDbAsync(db => BackofficeInboxProcessor.ReplayDeadAsync(db, host.Time.Now)));

        Assert.Equal(300, await CreditsAsync(host, user.Id));
        Assert.All(await host.ItemsAsync(), i => Assert.NotNull(i.ProcessedAt));
        var grant = Assert.Single(await host.WithDbAsync(db => db.CreditGrants.AsNoTracking().ToListAsync()));
        Assert.Equal("cs_paid", grant.GrantKey);
        Assert.Equal("pi_cs_paid", grant.PaymentIntentId);
    }

    private static Task<int> CreditsAsync(InboxTestHost host, Guid userId) =>
        host.WithDbAsync(db => db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.CreditsRemaining).SingleAsync());
}

/// <summary>Keeps the formatted messages of every error logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors) return _errors.ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            lock (owner._errors) owner._errors.Add(formatter(state, exception));
        }
    }
}
