using System.Net;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Backoffice;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary>Records templated e-mails; <see cref="Queue"/> decides between the outbox and a direct send.</summary>
internal sealed class FakeTemplatedEmailSender : ITemplatedEmailSender
{
    public List<(string Template, string To, string Locale, IReadOnlyDictionary<string, string?> Variables, bool Queued)> Sent { get; } = new();

    /// <summary>True: behaves like Email=On (queued with the caller's changes).</summary>
    public bool Queue { get; set; }

    /// <summary>Direct sends to these addresses throw.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryEnqueue(string templateKey, string toEmail, string locale, IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null)
    {
        if (!Queue) return false;
        lock (Sent) Sent.Add((templateKey, toEmail, locale, variables, true));
        return true;
    }

    public Task SendAsync(string templateKey, string toEmail, string locale, IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null)
    {
        if (Failing.Contains(toEmail)) throw new HttpRequestException("send failed");
        lock (Sent) Sent.Add((templateKey, toEmail, locale, variables, false));
        return Task.CompletedTask;
    }
}

internal static class PriceEvents
{
    /// <summary><c>price.scheduled</c> data: 300 credits 1425 → 1500 cents EUR, effective 2026-10-28.</summary>
    public static string Scheduled(int version = 2, string effectiveFrom = "2026-10-28T00:00:00Z", params object[] changes) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = version,
            ["label"] = "v" + version,
            ["summary"] = null,
            ["effective_from"] = effectiveFrom,
            ["notice_days"] = 30,
            ["changes"] = changes.Length > 0 ? changes : [Change(300, 1425, 1500)],
            ["removed_lookup_keys"] = Array.Empty<string>(),
        });

    public static Dictionary<string, object?> Change(int quantity, long? from, long? to, string currency = "eur", string? item = null) => new()
    {
        ["lookup_key"] = $"pd_credits_{quantity}_once_{currency}",
        ["item"] = item ?? $"credits_{quantity}",
        ["currency"] = currency,
        ["interval"] = "one_time",
        ["from"] = from,
        ["to"] = to,
    };

    public static PriceChangeEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PriceChangeEvent.TryParse(document.RootElement.Clone(), out _)!;
    }
}

public class PriceNoticePlannerTests
{
    private static readonly IReadOnlyList<CreditPack> Current =
        PricingTestData.Imported.Select(p => PricingTestData.Pack(p.Quantity, p.Cents, $"pd_credits_{p.Quantity}_once_eur")).ToList();

    private static PriceNoticeCandidate User(int quantity, bool enabled = true, bool enterprise = false, int? notified = null) =>
        new(Guid.NewGuid(), enabled, quantity, enterprise, notified);

    [Fact]
    public void ChangeOf300_NotifiesOnlyUsersWithAutoRechargeOf300()
    {
        var u300 = User(300);
        var plan = PriceNoticePlanner.Plan(PriceEvents.Parse(PriceEvents.Scheduled()), "EUR", Current, [u300, User(100), User(500), User(1000)]);

        var notice = Assert.Single(plan.Notices);
        Assert.Equal(u300.UserId, notice.UserId);
        Assert.Equal(new PackPriceChange(300, "pd_credits_300_once_eur", 1425, 1500, "EUR"), notice.Change);
    }

    [Fact]
    public void ChangesOfOtherPacksOnly_NotifyNobody()
    {
        var data = PriceEvents.Parse(PriceEvents.Scheduled(2, "2026-10-28T00:00:00Z", PriceEvents.Change(500, 2250, 2400)));

        Assert.Empty(PriceNoticePlanner.Plan(data, "EUR", Current, [User(300), User(100)]).Notices);
    }

    [Fact]
    public void EnterpriseAndDisabledAutoRecharge_AreNotNotified()
    {
        var plan = PriceNoticePlanner.Plan(PriceEvents.Parse(PriceEvents.Scheduled()), "EUR", Current,
            [User(300, enterprise: true), User(300, enabled: false), User(0)]);

        Assert.Empty(plan.Notices);
    }

    [Fact]
    public void RepeatedDelivery_DoesNotNotifyAgain()
    {
        var plan = PriceNoticePlanner.Plan(PriceEvents.Parse(PriceEvents.Scheduled(2)), "EUR", Current,
            [User(300, notified: 2), User(300, notified: 1)]);

        Assert.Single(plan.Notices);
        Assert.Equal(1, plan.AlreadyNotified);
    }

    [Fact]
    public void OtherCurrencyOtherItemsAndUnchangedAmounts_AreIgnored()
    {
        var data = PriceEvents.Parse(PriceEvents.Scheduled(2, "2026-10-28T00:00:00Z",
            PriceEvents.Change(300, 35000, 37000, currency: "czk"),
            PriceEvents.Change(300, 1425, 1425),
            PriceEvents.Change(300, 100, 200, item: "seal_addon")));

        Assert.Empty(PriceNoticePlanner.CreditChanges(data, "EUR", Current));
    }

    [Fact]
    public void NewPriceWithoutFrom_UsesThePriceInForce_AndMissingItemFallsBackToTheLookupKey()
    {
        var change = PriceEvents.Change(100, null, 600);
        change.Remove("item");
        var data = PriceEvents.Parse(PriceEvents.Scheduled(2, "2026-10-28T00:00:00Z", change));

        var changes = PriceNoticePlanner.CreditChanges(data, "eur", Current);

        Assert.Equal(new PackPriceChange(100, "pd_credits_100_once_eur", 500, 600, "EUR"), changes[100]);
    }

    [Fact]
    public void TieredNewPrice_IsReportedAndSkipped()
    {
        var problems = new List<string>();
        var data = PriceEvents.Parse(PriceEvents.Scheduled(2, "2026-10-28T00:00:00Z", PriceEvents.Change(300, 1425, null)));

        Assert.Empty(PriceNoticePlanner.CreditChanges(data, "EUR", Current, problems));
        Assert.Single(problems);
    }

    [Fact]
    public void Parse_RejectsDataWithoutVersion()
    {
        using var document = JsonDocument.Parse("""{"effective_from":"2026-10-28T00:00:00Z"}""");

        Assert.Null(PriceChangeEvent.TryParse(document.RootElement, out var problem));
        Assert.Contains("version", problem);
    }

    [Theory]
    [InlineData("2026-10-28T00:00:00Z", 30)]
    [InlineData("2026-09-28T12:00:01Z", 1)]
    [InlineData("2026-09-28T00:00:00Z", 0)]
    public void DaysUntil_RoundsUp(string effectiveFrom, int days)
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(days, PriceNoticePlanner.DaysUntil(now, DateTimeOffset.Parse(effectiveFrom)));
    }

    [Theory]
    [InlineData(1500, "15")]
    [InlineData(1425, "14.25")]
    [InlineData(1450, "14.5")]
    public void FormatMinor_ShowsMajorUnits(long minor, string expected) =>
        Assert.Equal(expected, PriceNoticePlanner.FormatMinor(minor));
}

public class PriceScheduledHandlerTests
{
    [Fact]
    public async Task ScheduledChangeOf300_NotifiesEachAffectedUserOnce_ViaWebhookAndPolling()
    {
        using var host = new PricingTestHost("On");
        var u300 = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; u.AutoRechargeCancelToken = "tok"; u.Locale = "cs"; });
        var u100 = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 100; });
        var enterprise = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; u.IsEnterprise = true; });

        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled(), "msg_1", "webhook");
        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled(), "msg_1", "poll");

        var sent = Assert.Single(host.Email.Sent);
        Assert.Equal(EmailTemplateId.PriceChangeNotice, sent.Template);
        Assert.Equal(u300.Email, sent.To);
        Assert.Equal("cs", sent.Locale);
        Assert.Equal("30", sent.Variables["daysNotice"]);
        Assert.Equal("300", sent.Variables["quantity"]);
        Assert.Equal("14.25", sent.Variables["oldPrice"]);
        Assert.Equal("15", sent.Variables["newPrice"]);
        Assert.Equal("EUR", sent.Variables["currency"]);
        Assert.Equal("https://app.example.com/Billing/AutoRecharge/Cancel?token=tok", sent.Variables["cancelUrl"]);
        Assert.Equal("https://app.example.com/Billing", sent.Variables["billingUrl"]);
        Assert.Equal(2, (await host.UserAsync(u300.Id)).PriceChangeNotifiedVersion);
        Assert.Null((await host.UserAsync(u100.Id)).PriceChangeNotifiedVersion);
        Assert.Null((await host.UserAsync(enterprise.Id)).PriceChangeNotifiedVersion);
    }

    [Fact]
    public async Task EmailOn_QueuesTheNoticeWithTheVersion()
    {
        using var host = new PricingTestHost("On");
        host.Email.Queue = true;
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });

        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled());

        Assert.True(Assert.Single(host.Email.Sent).Queued);
        Assert.Equal(2, (await host.UserAsync(user.Id)).PriceChangeNotifiedVersion);
    }

    [Fact]
    public async Task FailedSend_ThrowsAndTheRetryNotifiesOnlyTheRest()
    {
        using var host = new PricingTestHost("On");
        var ok = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        var failing = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        host.Email.Failing.Add(failing.Email);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled()));
        Assert.Null((await host.UserAsync(failing.Id)).PriceChangeNotifiedVersion);

        host.Email.Failing.Clear();
        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled());

        Assert.Equal(1, host.Email.Sent.Count(s => s.To == ok.Email));
        Assert.Equal(1, host.Email.Sent.Count(s => s.To == failing.Email));
    }

    [Fact]
    public async Task Shadow_SendsNothing()
    {
        using var host = new PricingTestHost("Shadow");
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });

        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled());

        Assert.Empty(host.Email.Sent);
        Assert.Null((await host.UserAsync(user.Id)).PriceChangeNotifiedVersion);
    }

    [Fact]
    public async Task Unscheduled_ResetsTheVersion_SoANewScheduleNotifiesAgain()
    {
        using var host = new PricingTestHost("On");
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled(), "msg_1");

        await host.HandleAsync(BackofficeEventTypes.PriceUnscheduled, """{"version":2,"label":"v2","previous_effective_from":"2026-10-28T00:00:00Z"}""", "msg_2");
        Assert.Null((await host.UserAsync(user.Id)).PriceChangeNotifiedVersion);

        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled(2, "2026-11-28T00:00:00Z"), "msg_3");
        Assert.Equal(2, host.Email.Sent.Count);
    }

    [Fact]
    public async Task SyncFailed_IsLoggedAsAnError()
    {
        var logger = new ListLogger<PriceSyncFailedHandler>();
        var handler = new PriceSyncFailedHandler(logger);
        using var data = JsonDocument.Parse("""{"version":3,"label":"v3","effective_from":"2026-10-01T00:00:00Z","error":{"code":"stripe_error","message":"No such product"},"attempts":2,"next_attempt_at":null,"blocks_activation":true}""");

        await handler.HandleAsync(new BackofficeEvent("msg_9", BackofficeEventTypes.PriceSyncFailed, data.RootElement.Clone(), "webhook", DateTimeOffset.UtcNow, 1), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("v3", entry.Message);
        Assert.Contains("stripe_error", entry.Message);
    }
}

public class PriceEffectiveHandlerTests
{
    [Fact]
    public async Task Effective_StoresTheNewPricePer100AndClearsTheNotices()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        var user = await host.AddUserAsync(u =>
        {
            u.AutoRechargeEnabled = true;
            u.AutoRechargeQuantity = 300;
            u.AutoRechargePricePer100 = 5m;
            u.PriceChangeNotifiedVersion = 2;
            u.PriceChangeNotifiedAt = host.Time.GetUtcNow();
        });
        var later = await host.AddUserAsync(u => u.PriceChangeNotifiedVersion = 3);
        host.EnqueuePriceList(PricingTestData.ImportedJson(2, new Dictionary<int, long> { [100] = 600, [300] = 1500 }), "\"v2\"");

        await host.HandleAsync(BackofficeEventTypes.PriceEffective, """{"version":2,"effective_from":"2026-09-28T00:00:00Z","changes":[]}""");

        var stored = await host.UserAsync(user.Id);
        Assert.Equal(6m, stored.AutoRechargePricePer100);
        Assert.Null(stored.PriceChangeNotifiedVersion);
        Assert.Null(stored.PriceChangeNotifiedAt);
        Assert.Equal(3, (await host.UserAsync(later.Id)).PriceChangeNotifiedVersion);
        // Auto-recharge charges the pack's price in force.
        Assert.Equal(1500, host.Provider.GetPack(300)!.UnitAmountMinor);
    }

    [Fact]
    public async Task PriceListOlderThanTheEvent_Throws()
    {
        using var host = new PricingTestHost("On");
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 100; u.AutoRechargePricePer100 = 5m; });
        host.EnqueuePriceList(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [100] = 600 }), "\"v1\"");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.HandleAsync(BackofficeEventTypes.PriceEffective, """{"version":2}"""));

        Assert.Equal(5m, (await host.UserAsync(user.Id)).AutoRechargePricePer100);
    }

    [Fact]
    public async Task Shadow_OnlyRefreshesThePriceList()
    {
        using var host = new PricingTestHost("Shadow");
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 100; u.AutoRechargePricePer100 = 5m; });
        host.EnqueuePriceList(PricingTestData.ImportedJson(2, new Dictionary<int, long> { [100] = 600 }), "\"v2\"");

        await host.HandleAsync(BackofficeEventTypes.PriceEffective, """{"version":2}""");

        Assert.Equal(2, host.Holder.Current!.Version);
        Assert.Equal(5m, (await host.UserAsync(user.Id)).AutoRechargePricePer100);
    }
}

public class PricingUpcomingCheckTests
{
    private static string Upcoming(int version, string effectiveFrom, long cents300) =>
        $$"""{"upcoming":{{PricingTestData.ImportedJson(version, new Dictionary<int, long> { [300] = cents300 })
            .Replace("\"effective_from\":\"2026-09-28T00:00:00Z\"", $"\"effective_from\":\"{effectiveFrom}\"")
            .Replace("\"status\":\"effective\"", "\"status\":\"scheduled\"")}},"scheduled":[]}""";

    private static void EnqueueJson(PricingTestHost host, string json) =>
        host.Service.Responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    private static PricingUpcomingCheck Check(PricingTestHost host) =>
        host.Services.GetServices<IHostedService>().OfType<PricingUpcomingCheck>().Single();

    [Fact]
    public async Task MissedScheduledEvent_SendsTheNoticesFromUpcoming()
    {
        using var host = new PricingTestHost("On");
        var user = await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        EnqueueJson(host, Upcoming(2, "2026-10-20T00:00:00Z", 1500));

        var result = await Check(host).RunOnceAsync(CancellationToken.None);

        Assert.Equal("v1/pricing/upcoming", host.Request(0).RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal(new PriceNoticeResult(1, 1), result);
        var sent = Assert.Single(host.Email.Sent);
        Assert.Equal("14.25", sent.Variables["oldPrice"]);
        Assert.Equal("15", sent.Variables["newPrice"]);
        Assert.Equal(2, (await host.UserAsync(user.Id)).PriceChangeNotifiedVersion);

        // A late webhook afterwards changes nothing.
        await host.HandleAsync(BackofficeEventTypes.PriceScheduled, PriceEvents.Scheduled(2, "2026-10-20T00:00:00Z"));
        Assert.Single(host.Email.Sent);
    }

    [Fact]
    public async Task BeforeTheNoticePeriod_DoesNothing()
    {
        using var host = new PricingTestHost("On");
        await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        EnqueueJson(host, Upcoming(2, "2026-12-01T00:00:00Z", 1500));

        Assert.Null(await Check(host).RunOnceAsync(CancellationToken.None));
        Assert.Empty(host.Email.Sent);
    }

    [Fact]
    public async Task NoticesAlreadySent_DoesNothing()
    {
        using var host = new PricingTestHost("On");
        await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; u.PriceChangeNotifiedVersion = 2; });
        await host.AddUserAsync(u => { u.AutoRechargeEnabled = true; u.AutoRechargeQuantity = 300; });
        EnqueueJson(host, Upcoming(2, "2026-10-20T00:00:00Z", 1500));

        Assert.Null(await Check(host).RunOnceAsync(CancellationToken.None));
        Assert.Empty(host.Email.Sent);
    }

    [Fact]
    public async Task NothingUpcoming_DoesNothing()
    {
        using var host = new PricingTestHost("On");
        EnqueueJson(host, """{"upcoming":null,"scheduled":[]}""");

        Assert.Null(await Check(host).RunOnceAsync(CancellationToken.None));
    }
}

public class PriceNoticeRegistrationTests
{
    private static bool Monitor(PricingTestHost host) =>
        host.Services.GetServices<IHostedService>().OfType<PriceChangeMonitorService>().Any();

    private static IReadOnlyList<Type> Handlers(PricingTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetServices<IBackofficeEventHandler>().Select(h => h.GetType()).ToList();
    }

    [Fact]
    public void Off_RunsTheMonitorAndNoPriceHandlers()
    {
        using var host = new PricingTestHost("Off");

        Assert.True(Monitor(host));
        Assert.Empty(Handlers(host));
        Assert.Empty(host.Services.GetServices<IHostedService>().OfType<PricingUpcomingCheck>());
    }

    [Fact]
    public void Shadow_RunsTheMonitorAndLogOnlyHandlers()
    {
        using var host = new PricingTestHost("Shadow");

        Assert.True(Monitor(host));
        Assert.Contains(typeof(PriceScheduledHandler), Handlers(host));
    }

    [Fact]
    public void On_ReplacesTheMonitorWithTheHandlers()
    {
        using var host = new PricingTestHost("On");

        Assert.False(Monitor(host));
        Assert.Equal(
            [typeof(PriceEffectiveHandler), typeof(PriceScheduledHandler), typeof(PriceSyncFailedHandler), typeof(PriceUnscheduledHandler)],
            Handlers(host).OrderBy(t => t.Name).ToArray());
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<PricingUpcomingCheck>());
        using var scope = host.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<EventHandlerRegistry>();
        Assert.IsType<PriceScheduledHandler>(registry.Find(BackofficeEventTypes.PriceScheduled));
        Assert.IsType<PriceUnscheduledHandler>(registry.Find(BackofficeEventTypes.PriceUnscheduled));
        Assert.IsType<PriceSyncFailedHandler>(registry.Find(BackofficeEventTypes.PriceSyncFailed));
    }

    [Theory]
    [InlineData(false, "Off")]
    [InlineData(false, "Shadow")]
    [InlineData(false, "On")]
    [InlineData(true, "On")] // PrivateServer forces Off
    public void Integration_RegistersTheMonitorWhenPricingIsNotOn(bool privateServer, string pricing)
    {
        var monitor = privateServer || pricing != "On";

        var services = BackofficeRegistrationTests.Register(new()
        {
            ["P4Backoffice:Modules:Pricing"] = pricing,
            ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
            ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        }, privateServer);

        Assert.Equal(monitor, services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PriceChangeMonitorService)));
    }
}
