using System.Net;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Erasure;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Erasure;

/// <summary>Payment provider stand-in: counts calls, fails on demand, reports cards left.</summary>
internal sealed class FakePaymentGateway : IPaymentGateway
{
    public int Detached { get; private set; }
    public bool FailDetach { get; set; }
    public bool CardLeft { get; set; }

    public Task DetachPaymentMethodsAsync(User user, CancellationToken cancellationToken = default)
    {
        if (FailDetach) throw new HttpRequestException("provider down");
        Detached++;
        return Task.CompletedTask;
    }

    public Task<bool> HasSavedCardAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(CardLeft);

    public Task EnsureCustomerAsync(User user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> StartCheckoutAsync(User user, CreditPack pack, string successUrl, string cancelUrl, IDictionary<string, string> metadata, bool saveCard, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> StartCardSetupAsync(User user, string successUrl, string cancelUrl, IDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CheckoutSessionInfo?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(User user, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> CreatePortalSessionAsync(User user, string returnUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SavedPaymentMethod?> GetSavedPaymentMethodAsync(User user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// Account erasure (#217) against a real PostgreSQL with every migration (triggers, FKs):
/// isolation between accounts, what is kept, idempotence, concurrency, billing failure, the
/// outbox kinds and the <c>subject.erasure.requested</c> handler.
/// </summary>
[Trait("Category", "Db")]
public class AccountErasureTests : IClassFixture<OutboxPostgresFixture>, IDisposable
{
    private readonly OutboxPostgresFixture _pg;
    private readonly OutboxTestHost _host;
    private readonly FakePaymentGateway _payments = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "erasure-test-" + Guid.NewGuid().ToString("N"));

    public AccountErasureTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
        Directory.CreateDirectory(_tempDir);
        _host = new OutboxTestHost(pg.Configure, services =>
        {
            services.AddSingleton<IPaymentGateway>(_payments);
            services.AddSingleton<IOptions<P4BackofficeProductOptions>>(Microsoft.Extensions.Options.Options.Create(Backoffice(billingOn: false)));
            services.AddScoped<AccountErasureService>();
            services.AddSingleton<IOutboxHandler, ErasureRequestOutboxHandler>();
            services.AddSingleton<IOutboxHandler, ErasureAckOutboxHandler>();
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static P4BackofficeProductOptions Backoffice(bool billingOn) => new()
    {
        Mode = "On",
        BaseUrl = OutboxTestHost.BaseUrl,
        SecretKey = OutboxTestHost.SecretKey,
        Environment = "test",
        Modules = new P4BackofficeProductOptions.ModulesOptions { Billing = billingOn ? "On" : null },
    };

    private AccountErasureService Erasure(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AccountErasureService>();

    private async Task<AccountErasureResult> DeleteAsync(Guid userId)
    {
        using var scope = _host.Services.CreateScope();
        return await Erasure(scope).DeleteAccountAsync(userId, CancellationToken.None);
    }

    /// <summary>The same shape of data for every account, so a sloppy filter would hit the other one.</summary>
    private sealed record Seeded(Guid UserId, string Email, string File, Guid PendingEmailItem, Guid PendingConsentItem);

    private async Task<Seeded> SeedAsync(string email, string? stripeCustomer = null)
    {
        await using var db = _pg.CreateContext();
        var user = new User
        {
            Email = email,
            EmailVerified = true,
            IsActive = true,
            StripeCustomerId = stripeCustomer,
            AutoRechargeEnabled = true,
            AutoRechargeQuantity = 100,
            EmailOtpCode = "otp-hash",
            PasswordResetToken = "reset-hash",
            Locale = "cs",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var id = user.Id;

        var file = Path.Combine(_tempDir, id.ToString("N") + ".pdf");
        await File.WriteAllTextAsync(file, "%PDF-1.7");
        var document = new Document { UserId = id };
        db.Documents.Add(document);
        db.UsageRecords.Add(new UsageRecord { UserId = id, DocumentId = document.Id, Operation = "sign" });
        db.SigningData.Add(new SigningData { UserId = id, PresignedPdfPath = file, HashToSign = "h", CertificatePem = "c" });
        db.StoredPdfTemplates.Add(new StoredPdfTemplate { UserId = id, Base64Content = "JVBERi0=", FieldsJson = "[]", Name = "t" });
        db.ApiOperations.Add(new ApiOperation { UserId = id, OperationId = "op-" + id.ToString("N"), Operation = "sign", RequestFingerprint = "f", State = ApiOperationState.Completed, ResultProtected = [1, 2, 3] });
        db.ApiTokens.Add(new ApiToken { UserId = id, Label = "t", TokenHash = [1, 2, 3], TokenPrefix = "p4_" + id.ToString("N")[..5] });
        var invoice = new Invoice { UserId = id, AmountCents = 1000, StripeInvoiceId = "in_" + id.ToString("N") };
        db.Invoices.Add(invoice);
        db.Payments.Add(new Payment { UserId = id, InvoiceId = invoice.Id, AmountCents = 1000 });
        db.ConsentRecords.Add(new ConsentRecord
        {
            UserId = id,
            SubjectRef = AccountErasure.SubjectRef(id),
            Document = "terms",
            Purpose = "terms",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            UserAgent = "Mozilla/5.0 test",
        });
        db.BackofficeWebhookInboxItems.Add(new BackofficeWebhookInboxItem
        {
            WebhookId = "evt-email-" + id.ToString("N"),
            Type = "email.bounced",
            PayloadJson = $$"""{"to":"{{email}}"}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        db.BackofficeWebhookInboxItems.Add(new BackofficeWebhookInboxItem
        {
            WebhookId = "evt-ref-" + id.ToString("N"),
            Type = "billing.payment.succeeded",
            PayloadJson = $$"""{"customer_ref":"user:{{id:D}}"}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        if (stripeCustomer != null)
        {
            db.WebhookEvents.Add(new WebhookEvent
            {
                EventId = "evt_" + id.ToString("N"),
                EventType = "invoice.paid",
                PayloadJson = $$"""{"customer":"{{stripeCustomer}}"}""",
                ProcessedAt = DateTimeOffset.UtcNow,
            });
        }
        db.AuthAttemptCounters.Add(new AuthAttemptCounter { Key = "signin-account:" + email.ToLowerInvariant(), Count = 1, WindowStart = DateTimeOffset.UtcNow });
        db.AuthAttemptCounters.Add(new AuthAttemptCounter { Key = "reauth:" + id.ToString("D"), Count = 1, WindowStart = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        using var scope = _host.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();
        var mail = outbox.Enqueue("email.raw", new { to = email, subject = "s", html = "h" }, subjectRef: "user:" + id.ToString("D"));
        var consent = outbox.Enqueue("consent", new { events = Array.Empty<object>() }, subjectRef: AccountErasure.SubjectRef(id));
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync();
        return new Seeded(id, email, file, mail, consent);
    }

    private async Task AssertUntouchedAsync(Seeded s)
    {
        await using var db = _pg.CreateContext();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == s.UserId);
        Assert.Equal(s.Email, u.Email);
        Assert.True(u.IsActive);
        Assert.Null(u.ErasedAt);
        Assert.True(u.AutoRechargeEnabled);
        Assert.Equal("otp-hash", u.EmailOtpCode);
        Assert.Equal(1, await db.Documents.CountAsync(d => d.UserId == s.UserId));
        Assert.Equal(1, await db.UsageRecords.CountAsync(r => r.UserId == s.UserId && r.DocumentId != null));
        Assert.Equal(1, await db.SigningData.CountAsync(d => d.UserId == s.UserId));
        Assert.Equal(1, await db.StoredPdfTemplates.CountAsync(t => t.UserId == s.UserId));
        Assert.Equal(1, await db.ApiOperations.CountAsync(o => o.UserId == s.UserId));
        Assert.Equal(1, await db.ApiTokens.CountAsync(t => t.UserId == s.UserId));
        Assert.Equal("Mozilla/5.0 test", (await db.ConsentRecords.SingleAsync(c => c.UserId == s.UserId)).UserAgent);
        Assert.True(await db.BackofficeWebhookInboxItems.AnyAsync(i => i.WebhookId == "evt-email-" + s.UserId.ToString("N")));
        Assert.True(await db.BackofficeWebhookInboxItems.AnyAsync(i => i.WebhookId == "evt-ref-" + s.UserId.ToString("N")));
        Assert.True(await db.AuthAttemptCounters.AnyAsync(c => c.Key == "signin-account:" + s.Email.ToLowerInvariant()));
        foreach (var itemId in new[] { s.PendingEmailItem, s.PendingConsentItem })
        {
            var item = await db.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
            Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
            Assert.NotNull(item.PayloadProtected);
        }
        Assert.True(File.Exists(s.File));
    }

    [DockerFact]
    public async Task Erasing_A_erases_A_and_leaves_B_with_a_similar_address_untouched()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        // B's address contains A's, and B's Stripe customer id starts with A's: substring matches would hit B.
        var a = await SeedAsync($"a{tag}@example.com", stripeCustomer: "cus_" + tag);
        var b = await SeedAsync($"xa{tag}@example.com", stripeCustomer: "cus_" + tag + "B");

        var result = await DeleteAsync(a.UserId);

        Assert.Equal(AccountErasureStatus.Erased, result.Status);
        Assert.Equal(AccountErasure.RetainedReasons.Accounting, result.RetainedReason);
        Assert.Equal(1, _payments.Detached); // only A's cards

        await using (var db = _pg.CreateContext())
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == a.UserId);
            Assert.Equal(AccountErasure.TombstoneEmail(a.UserId), u.Email);
            Assert.DoesNotContain(tag, u.Email);
            Assert.False(u.IsActive);
            Assert.NotNull(u.ErasedAt);
            Assert.Null(u.StripeCustomerId);
            Assert.Null(u.EmailOtpCode);
            Assert.Null(u.PasswordResetToken);
            Assert.Null(u.Locale);
            Assert.False(u.AutoRechargeEnabled);

            // Gone.
            Assert.Equal(0, await db.Documents.CountAsync(d => d.UserId == a.UserId));
            Assert.Equal(0, await db.SigningData.CountAsync(d => d.UserId == a.UserId));
            Assert.Equal(0, await db.StoredPdfTemplates.CountAsync(t => t.UserId == a.UserId));
            Assert.Equal(0, await db.ApiOperations.CountAsync(o => o.UserId == a.UserId));
            Assert.Equal(0, await db.ApiTokens.CountAsync(t => t.UserId == a.UserId));
            Assert.False(await db.BackofficeWebhookInboxItems.AnyAsync(i => i.WebhookId == "evt-email-" + a.UserId.ToString("N")));
            Assert.False(await db.BackofficeWebhookInboxItems.AnyAsync(i => i.WebhookId == "evt-ref-" + a.UserId.ToString("N")));
            Assert.False(await db.WebhookEvents.AnyAsync(w => w.EventId == "evt_" + a.UserId.ToString("N")));
            Assert.False(await db.AuthAttemptCounters.AnyAsync(c => c.Key.EndsWith(a.Email.ToLowerInvariant()) && !c.Key.EndsWith(b.Email.ToLowerInvariant())));
            Assert.False(await db.AuthAttemptCounters.AnyAsync(c => c.Key == "reauth:" + a.UserId.ToString("D")));

            // Kept: invoices and payments (10 years), usage on the tombstone, consents without the user agent.
            Assert.Equal(1, await db.Invoices.CountAsync(i => i.UserId == a.UserId));
            Assert.Equal(1, await db.Payments.CountAsync(p => p.UserId == a.UserId));
            var usage = await db.UsageRecords.SingleAsync(r => r.UserId == a.UserId);
            Assert.Null(usage.DocumentId);
            var consent = await db.ConsentRecords.SingleAsync(c => c.UserId == a.UserId);
            Assert.Null(consent.UserAgent);
            Assert.Equal(AccountErasure.SubjectRef(a.UserId), consent.SubjectRef);

            // A's unsent messages are cancelled without payload.
            foreach (var itemId in new[] { a.PendingEmailItem, a.PendingConsentItem })
            {
                var item = await db.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
                Assert.Equal(BackofficeOutboxStatus.Cancelled, item.Status);
                Assert.Null(item.PayloadProtected);
            }

            // Audit without the address.
            var audit = await db.AdminAuditEntries.AsNoTracking().SingleAsync(e => e.TargetUserId == a.UserId && e.Action == AccountErasureService.AuditAction);
            Assert.Equal("", audit.ActorEmail);
            Assert.DoesNotContain(tag, audit.Change);
        }
        Assert.False(File.Exists(a.File));

        await AssertUntouchedAsync(b);
        await using (var db = _pg.CreateContext())
        {
            Assert.True(await db.WebhookEvents.AnyAsync(w => w.EventId == "evt_" + b.UserId.ToString("N")));
            Assert.Equal("cus_" + tag + "B", (await db.Users.SingleAsync(u => u.Id == b.UserId)).StripeCustomerId);
        }
    }

    [DockerFact]
    public async Task The_consent_trigger_still_refuses_every_other_change()
    {
        var a = await SeedAsync($"t{Guid.NewGuid():N}@example.com");
        await using var db = _pg.CreateContext();
        // Outside the erasure's transaction setting nothing may change.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"ConsentRecords\" SET \"UserAgent\" = NULL WHERE \"UserId\" = {0}", a.UserId));
        // With it, only UserAgent → NULL; any other column, and DELETE, are still refused.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL dotnet_signing.erasure = 'on'");
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"ConsentRecords\" SET \"Version\" = 2, \"UserAgent\" = NULL WHERE \"UserId\" = {0}", a.UserId));
        await tx.RollbackAsync();
        await using var tx2 = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL dotnet_signing.erasure = 'on'");
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"ConsentRecords\" WHERE \"UserId\" = {0}", a.UserId));
        await tx2.RollbackAsync();
    }

    private async Task<List<BackofficeOutboxItem>> ErasureItemsAsync(Guid userId)
    {
        await using var db = _pg.CreateContext();
        var subjectRef = AccountErasure.SubjectRef(userId);
        return await db.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.SubjectRef == subjectRef && i.Kind.StartsWith("subject."))
            .OrderBy(i => i.CreatedAt).ToListAsync();
    }

    private string Payload(BackofficeOutboxItem item) =>
        _host.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(item.PayloadProtected!);

    [DockerFact]
    public async Task Deleting_twice_erases_once_and_queues_one_request_with_the_identifiers()
    {
        var a = await SeedAsync($"twice{Guid.NewGuid():N}@example.com", stripeCustomer: "cus_" + Guid.NewGuid().ToString("N")[..10]);

        Assert.Equal(AccountErasureStatus.Erased, (await DeleteAsync(a.UserId)).Status);
        var again = await DeleteAsync(a.UserId);
        Assert.Equal(AccountErasureStatus.AlreadyErased, again.Status);
        Assert.Equal(1, _payments.Detached); // the tombstone has no payer left

        var items = await ErasureItemsAsync(a.UserId);
        var item = Assert.Single(items);
        Assert.Equal(AccountErasure.RequestOutboxKind, item.Kind);
        using var json = JsonDocument.Parse(Payload(item));
        Assert.Equal(AccountErasure.SubjectRef(a.UserId), json.RootElement.GetProperty("subject_ref").GetString());
        Assert.Equal("account_deleted", json.RootElement.GetProperty("origin").GetString());
        Assert.Equal(a.Email, json.RootElement.GetProperty("emails")[0].GetString());
        Assert.False(json.RootElement.TryGetProperty("customer_ref", out _)); // Billing module Off: no customer in the service
        Assert.False(json.RootElement.TryGetProperty("phones", out _));

        await using var db = _pg.CreateContext();
        Assert.Equal(1, await db.AdminAuditEntries.CountAsync(e => e.TargetUserId == a.UserId && e.Action == AccountErasureService.AuditAction));
    }

    [DockerFact]
    public async Task Two_erasures_at_once_end_with_one_tombstone_and_one_request()
    {
        var a = await SeedAsync($"race{Guid.NewGuid():N}@example.com");

        var results = await Task.WhenAll(DeleteAsync(a.UserId), DeleteAsync(a.UserId));

        Assert.Single(results, r => r.Status == AccountErasureStatus.Erased);
        Assert.Single(results, r => r.Status == AccountErasureStatus.AlreadyErased);
        Assert.Single(await ErasureItemsAsync(a.UserId));
    }

    [DockerFact]
    public async Task When_cards_cannot_be_removed_nothing_is_erased_or_sent()
    {
        var a = await SeedAsync($"card{Guid.NewGuid():N}@example.com", stripeCustomer: "cus_" + Guid.NewGuid().ToString("N")[..10]);

        _payments.FailDetach = true;
        Assert.Equal(AccountErasureStatus.BillingFailed, (await DeleteAsync(a.UserId)).Status);
        _payments.FailDetach = false;
        _payments.CardLeft = true;
        Assert.Equal(AccountErasureStatus.BillingFailed, (await DeleteAsync(a.UserId)).Status);

        await AssertUntouchedAsync(a);
        Assert.Empty(await ErasureItemsAsync(a.UserId));
    }

    [DockerFact]
    public async Task The_request_is_retried_past_the_usual_give_up_time_and_dies_loudly_only_after_30_days()
    {
        var a = await SeedAsync($"retry{Guid.NewGuid():N}@example.com");
        await DeleteAsync(a.UserId);
        var id = Assert.Single(await ErasureItemsAsync(a.UserId)).Id;

        _host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);
        var first = await _host.Processor.TryDispatchNowAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Pending, first.Status); // a failure is never a success

        var (request, body) = _host.Service.Requests.Last();
        Assert.Equal("/v1/erasures", request.RequestUri!.AbsolutePath);
        Assert.Equal(id.ToString(), request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single());
        Assert.Contains("\"subject_ref\":\"dotnet:user:", body);

        Assert.Equal(AccountErasure.RetryWindow, _host.Processor.GiveUpAfterFor(AccountErasure.RequestOutboxKind));
        Assert.Equal(AccountErasure.RetryWindow, _host.Processor.GiveUpAfterFor(AccountErasure.AckOutboxKind));

        // Two days in (the default kinds give up after 20 h): still retried.
        await using (var db = _pg.CreateContext())
        {
            await db.BackofficeOutboxItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.FirstAttemptAt, _host.Time.GetUtcNow() - TimeSpan.FromDays(2))
                .SetProperty(i => i.CreatedAt, _host.Time.GetUtcNow() - TimeSpan.FromDays(4))
                .SetProperty(i => i.NextAttemptAt, _host.Time.GetUtcNow()));
        }
        _host.Service.Enqueue(HttpStatusCode.BadGateway);
        var later = await _host.Processor.TryDispatchNowAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Pending, later.Status);
        Assert.True(_host.Processor.IsOverdue(await _host.ItemAsync(id), _host.Time.GetUtcNow())); // past 72 h: every failure is an alert

        // Past 30 days: Dead (logged as an error, kept), never Sent.
        await using (var db = _pg.CreateContext())
        {
            await db.BackofficeOutboxItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.FirstAttemptAt, _host.Time.GetUtcNow() - TimeSpan.FromDays(31))
                .SetProperty(i => i.NextAttemptAt, _host.Time.GetUtcNow()));
        }
        _host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);
        var dead = await _host.Processor.TryDispatchNowAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Dead, dead.Status);
        Assert.NotNull((await _host.ItemAsync(id)).PayloadProtected); // kept for whoever settles it
    }

    [DockerFact]
    public async Task The_request_is_sent_once_the_service_answers_and_a_refusal_is_dead_not_sent()
    {
        var a = await SeedAsync($"ok{Guid.NewGuid():N}@example.com");
        await DeleteAsync(a.UserId);
        var id = Assert.Single(await ErasureItemsAsync(a.UserId)).Id;
        _host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"6f1c0000-0000-0000-0000-000000000001","status":"in_progress"}""");
        var sent = await _host.Processor.TryDispatchNowAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Sent, sent.Status);
        Assert.Null((await _host.ItemAsync(id)).PayloadProtected); // the addresses do not outlive the delivery

        var b = await SeedAsync($"mismatch{Guid.NewGuid():N}@example.com");
        await DeleteAsync(b.UserId);
        var idB = Assert.Single(await ErasureItemsAsync(b.UserId)).Id;
        _host.Service.EnqueueProblem(HttpStatusCode.Conflict, "erasure_identifiers_mismatch");
        var refused = await _host.Processor.TryDispatchNowAsync(idB, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Dead, refused.Status);
    }

    // ── subject.erasure.requested ──────────────────────────────────────────────────────

    private static BackofficeEvent RequestedEvent(Guid erasureId, string subjectRef, int attempt = 1, string status = "in_progress", bool productTarget = true)
    {
        var targets = productTarget
            ? """[{"target":"consents","status":"done","retained_reason":null},{"target":"product:dotnet-signing-server","status":"manual","retained_reason":null}]"""
            : """[{"target":"consents","status":"done","retained_reason":null}]""";
        var json = $$"""{"erasure_id":"{{erasureId:D}}","subject_ref":"{{subjectRef}}","status":"{{status}}","targets":{{targets}}}""";
        using var doc = JsonDocument.Parse(json);
        return new BackofficeEvent("wh_" + Guid.NewGuid().ToString("N"), BackofficeEventTypes.SubjectErasureRequested,
            doc.RootElement.Clone(), "webhook", DateTimeOffset.UtcNow, attempt);
    }

    private async Task HandleAsync(BackofficeEvent evt)
    {
        using var scope = _host.Services.CreateScope();
        var handler = new ErasureRequestedHandler(Erasure(scope), NullLoggers.For<ErasureRequestedHandler>());
        await handler.HandleAsync(evt, CancellationToken.None);
    }

    [DockerFact]
    public async Task A_subject_request_erases_the_account_and_acks_retained_and_a_duplicate_acks_the_same()
    {
        var a = await SeedAsync($"req{Guid.NewGuid():N}@example.com");
        var b = await SeedAsync($"other{Guid.NewGuid():N}@example.com");
        var erasureId = Guid.NewGuid();

        await HandleAsync(RequestedEvent(erasureId, AccountErasure.SubjectRef(a.UserId)));
        await HandleAsync(RequestedEvent(erasureId, AccountErasure.SubjectRef(a.UserId))); // redelivered

        await using (var db = _pg.CreateContext())
        {
            Assert.NotNull((await db.Users.SingleAsync(u => u.Id == a.UserId)).ErasedAt);
        }
        await AssertUntouchedAsync(b);

        var items = await ErasureItemsAsync(a.UserId);
        Assert.All(items, i => Assert.Equal(AccountErasure.AckOutboxKind, i.Kind)); // no POST /v1/erasures for a request of the service
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            using var json = JsonDocument.Parse(Payload(item));
            Assert.Equal("retained", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("accounting_10y", json.RootElement.GetProperty("retained_reason").GetString());
            Assert.Equal("dotnet-signing-server", json.RootElement.GetProperty("product").GetString());
        }

        // The ack goes to the target's path; a repeat on a settled target is what we wanted.
        _host.Service.Enqueue(HttpStatusCode.OK, "{}");
        var sent = await _host.Processor.TryDispatchNowAsync(items[0].Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Sent, sent.Status);
        var (request, body) = _host.Service.Requests.Last();
        Assert.Equal($"/v1/erasures/{erasureId:D}/targets/dotnet-signing-server/ack", request.RequestUri!.AbsolutePath);
        Assert.Contains("\"status\":\"retained\"", body);
        Assert.DoesNotContain("erasure_id", body);
        Assert.DoesNotContain("@", body);

        _host.Service.EnqueueProblem(HttpStatusCode.Conflict, ErasureAckOutboxHandler.TargetSettled);
        var repeat = await _host.Processor.TryDispatchNowAsync(items[1].Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Sent, repeat.Status);
    }

    [DockerFact]
    public async Task A_request_for_an_unknown_account_acks_done()
    {
        var unknown = Guid.NewGuid();
        await HandleAsync(RequestedEvent(Guid.NewGuid(), AccountErasure.SubjectRef(unknown)));
        var item = Assert.Single(await ErasureItemsAsync(unknown));
        using var json = JsonDocument.Parse(Payload(item));
        Assert.Equal("done", json.RootElement.GetProperty("status").GetString());
    }

    [DockerFact]
    public async Task Foreign_refs_requests_without_a_product_target_and_unconfirmed_requests_are_ignored()
    {
        var a = await SeedAsync($"ign{Guid.NewGuid():N}@example.com");
        await HandleAsync(RequestedEvent(Guid.NewGuid(), "v0:user:42"));
        await HandleAsync(RequestedEvent(Guid.NewGuid(), AccountErasure.SubjectRef(a.UserId), productTarget: false));
        await HandleAsync(RequestedEvent(Guid.NewGuid(), AccountErasure.SubjectRef(a.UserId), status: "verifying"));
        await AssertUntouchedAsync(a);
        Assert.Empty(await ErasureItemsAsync(a.UserId));
    }

    [DockerFact]
    public async Task A_failing_request_throws_for_a_retry_and_tells_the_service_failed_once()
    {
        var a = await SeedAsync($"fail{Guid.NewGuid():N}@example.com", stripeCustomer: "cus_" + Guid.NewGuid().ToString("N")[..10]);
        var erasureId = Guid.NewGuid();
        _payments.FailDetach = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(RequestedEvent(erasureId, AccountErasure.SubjectRef(a.UserId), attempt: 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(RequestedEvent(erasureId, AccountErasure.SubjectRef(a.UserId), attempt: 2)));

        await AssertUntouchedAsync(a);
        var only = Assert.Single(await ErasureItemsAsync(a.UserId)); // one failed ack, no request
        Assert.Equal(AccountErasure.AckOutboxKind, only.Kind);
        Assert.Contains("\"status\":\"failed\"", Payload(only));
        Assert.Contains(erasureId.ToString("D"), Payload(only));
        await using var db = _pg.CreateContext();

        // Once the provider is back, the retry erases and acks the result.
        _payments.FailDetach = false;
        await HandleAsync(RequestedEvent(erasureId, AccountErasure.SubjectRef(a.UserId), attempt: 3));
        Assert.NotNull((await db.Users.AsNoTracking().SingleAsync(u => u.Id == a.UserId)).ErasedAt);
    }

    [Fact]
    public void The_tombstone_address_is_unique_per_account_and_says_nothing_about_the_person()
    {
        var id = Guid.NewGuid();
        var address = AccountErasure.TombstoneEmail(id);
        Assert.Matches("^erased\\+[0-9a-f]{32}@invalid$", address);
        Assert.Equal(address, AccountErasure.TombstoneEmail(id));
        Assert.NotEqual(address, AccountErasure.TombstoneEmail(Guid.NewGuid()));
        Assert.DoesNotContain(id.ToString("N"), address);
    }

    [Fact]
    public void Only_this_products_refs_parse()
    {
        var id = Guid.NewGuid();
        Assert.True(AccountErasure.TryParseSubjectRef("dotnet:user:" + id.ToString("D"), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(AccountErasure.TryParseSubjectRef("v0:user:42", out _));
        Assert.False(AccountErasure.TryParseSubjectRef("dotnet:user:42", out _));
        Assert.False(AccountErasure.TryParseSubjectRef(null, out _));
        Assert.False(AccountErasure.IsValidProductSlug("../x"));
        Assert.True(AccountErasure.IsValidProductSlug("dotnet-signing-server"));
    }
}
