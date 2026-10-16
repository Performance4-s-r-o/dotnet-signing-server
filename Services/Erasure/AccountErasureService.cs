using System.Security.Cryptography;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Erasure;

/// <summary>How an erasure ended.</summary>
public enum AccountErasureStatus
{
    /// <summary>Erased now.</summary>
    Erased,

    /// <summary>Was erased before; the sweep ran again and found (almost) nothing.</summary>
    AlreadyErased,

    /// <summary>No such account (never existed). Nothing to erase.</summary>
    NotFound,

    /// <summary>
    /// The saved cards could not be removed at the payment provider; nothing was erased and
    /// nothing was sent. The caller reports an error (the account page) or retries (webhook).
    /// </summary>
    BillingFailed,
}

/// <param name="Counts">What happened, for logs and the ack: no personal data.</param>
/// <param name="RetainedReason">Why something stays (<see cref="AccountErasure.RetainedReasons"/>); null = nothing.</param>
public sealed record AccountErasureResult(
    AccountErasureStatus Status,
    IReadOnlyDictionary<string, int> Counts,
    string? RetainedReason)
{
    public bool Succeeded => Status is AccountErasureStatus.Erased or AccountErasureStatus.AlreadyErased or AccountErasureStatus.NotFound;
}

/// <summary>An erasure asked for by the service (<c>subject.erasure.requested</c>): ack its product target.</summary>
public sealed record ErasureAckTarget(Guid ErasureId, string ProductSlug);

/// <summary>
/// Erases one account (GDPR art. 17, #217) — from the account page (<c>account_deleted</c>) or for
/// a request the service received (<c>subject_request</c>).
///
/// <list type="number">
/// <item>Saved cards are removed at the payment provider first (this product has no Stripe
/// subscription; auto-recharge on a saved card is its only recurring charge). Failure =
/// <see cref="AccountErasureStatus.BillingFailed"/>: nothing is erased, nothing sent.</item>
/// <item>One transaction, the user row locked (<c>FOR UPDATE</c>), so two erasures of the same
/// account run one after the other and the second finds a tombstone:
/// presigns, API operations, stored templates, API tokens and documents are deleted; usage
/// records stay (on the tombstone, billing history); consent records lose the user agent
/// (append-only otherwise, proof of consent); the subject's unsent outbox items are cancelled
/// and their payloads dropped; processed inbox and Stripe webhook rows naming the account are
/// deleted; sign-in attempt counters go; the user row becomes a tombstone without personal data
/// (invoices and payments point at it and are kept 10 years); an audit row without personal
/// data is written; and the message for the service — <c>POST /v1/erasures</c> or the ack — is
/// queued in the same commit (transactional outbox).</item>
/// <item>After the commit: the presign temp files are deleted (an orphan the sweep of
/// <c>PresignCleanupService</c> removes later) and the token cache forgets the tokens.</item>
/// </list>
///
/// Every step selects by this user's id (or this user's own address and Stripe customer, quoted
/// as whole JSON strings), so no other account's rows are touched. Running it again is safe.
/// Logs carry the user id, never the address.
///
/// Each call works in its own DI scope (own <see cref="ApplicationDbContext"/>): the inbox
/// processor's context holds the event row, and a retried transaction must start clean.
/// </summary>
public sealed class AccountErasureService
{
    public const string AuditAction = "user.erased";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(IServiceScopeFactory scopes, TimeProvider time, ILogger<AccountErasureService> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>The user deleted their own account.</summary>
    public Task<AccountErasureResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        EraseAsync(userId, AccountErasure.Origins.AccountDeleted, ack: null, cancellationToken);

    /// <summary>The service asks for the erasure; <paramref name="ack"/> is queued with the result.</summary>
    public Task<AccountErasureResult> EraseForRequestAsync(Guid userId, ErasureAckTarget ack, CancellationToken cancellationToken) =>
        EraseAsync(userId, AccountErasure.Origins.SubjectRequest, ack, cancellationToken);

    /// <summary>
    /// Queues a <c>failed</c> ack (the erasure threw; the inbox retries it and acks again).
    /// Own scope and commit, nothing else in it.
    /// </summary>
    public async Task QueueFailedAckAsync(Guid userId, ErasureAckTarget ack, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();
        EnqueueAck(outbox, ack, AccountErasure.AckStatuses.Failed, counts: null, retainedReason: null, AccountErasure.SubjectRef(userId));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<AccountErasureResult> EraseAsync(Guid userId, string origin, ErasureAckTarget? ack, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();

        var existing = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (existing == null)
        {
            if (ack != null)
            {
                // Nothing here belongs to this subject: the target is done.
                var outbox = services.GetRequiredService<IBackofficeOutbox>();
                EnqueueAck(outbox, ack, AccountErasure.AckStatuses.Done, new Dictionary<string, int> { ["accounts_found"] = 0 }, null,
                    AccountErasure.SubjectRef(userId));
                await db.SaveChangesAsync(cancellationToken);
            }
            _logger.LogInformation("Account erasure ({Origin}): user {UserId} does not exist; nothing to erase", origin, userId);
            return new AccountErasureResult(AccountErasureStatus.NotFound, new Dictionary<string, int>(), null);
        }

        if (existing.ErasedAt == null && !await StopRecurringChargesAsync(services, existing, cancellationToken))
        {
            return new AccountErasureResult(AccountErasureStatus.BillingFailed, new Dictionary<string, int>(), null);
        }

        var backoffice = services.GetRequiredService<IOptions<P4BackofficeProductOptions>>().Value;
        var strategy = db.Database.CreateExecutionStrategy();
        var (result, files, tokenIds) = await strategy.ExecuteAsync(
            ct => EraseLocallyAsync(services, db, userId, origin, ack, backoffice, ct), cancellationToken);

        foreach (var path in files) DeleteFile(path, userId);
        var apiAuth = services.GetService<IApiAuthService>();
        foreach (var tokenId in tokenIds) apiAuth?.InvalidateTokenCache(tokenId);

        _logger.LogInformation(
            "Account erasure ({Origin}) of user {UserId}: {Status}, {Counts}, retained {RetainedReason}",
            origin, userId, result.Status, string.Join(", ", result.Counts.Select(c => $"{c.Key}={c.Value}")), result.RetainedReason ?? "nothing");
        return result;
    }

    /// <summary>
    /// Removes the saved cards so no off-session charge (auto-recharge) can happen after the
    /// erasure, and checks that none is left. Users without a payer at the provider have none.
    /// </summary>
    private async Task<bool> StopRecurringChargesAsync(IServiceProvider services, User user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId)) return true;

        var payments = services.GetRequiredService<IPaymentGateway>();
        try
        {
            await payments.DetachPaymentMethodsAsync(user, cancellationToken);
            if (await payments.HasSavedCardAsync(user, cancellationToken))
            {
                _logger.LogError("Account erasure of user {UserId}: a saved card is still attached after detaching; the account is not erased", user.Id);
                return false;
            }
            return true;
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            // The customer is gone at Stripe: no card can be charged any more.
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Account erasure of user {UserId}: saved cards could not be removed; the account is not erased", user.Id);
            return false;
        }
    }

    private async Task<(AccountErasureResult Result, IReadOnlyList<string> Files, IReadOnlyList<Guid> TokenIds)> EraseLocallyAsync(
        IServiceProvider services,
        ApplicationDbContext db,
        Guid userId,
        string origin,
        ErasureAckTarget? ack,
        P4BackofficeProductOptions backoffice,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear(); // a retried attempt starts clean (this context is ours alone)
        var outbox = services.GetRequiredService<IBackofficeOutbox>();
        var npgsql = db.Database.IsNpgsql();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (npgsql)
        {
            var users = SqlNames.Of<User>(db);
            // Serialises erasures of one account; the loser re-reads a tombstone below.
            var lockSql = $"SELECT 1 FROM {users.Table} WHERE {users[nameof(User.Id)]} = {{0}} FOR UPDATE"; // names from the model
            await db.Database.ExecuteSqlRawAsync(lockSql, [userId], ct);
            // Lets the ConsentRecords trigger accept clearing UserAgent, in this transaction only.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL dotnet_signing.erasure = 'on'", ct);
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var firstTime = user.ErasedAt == null;
        var email = firstTime ? UserEmails.Trim(user.Email) : null;
        var stripeCustomerId = string.IsNullOrWhiteSpace(user.StripeCustomerId) ? null : user.StripeCustomerId;
        var userIdText = userId.ToString("D");
        var subjectRef = AccountErasure.SubjectRef(userId);
        var emailItemRef = "user:" + userIdText; // e-mail outbox items (TemplatedEmailSender)
        var now = _time.GetUtcNow();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        // Presigns and their temp PDFs (the files go after the commit).
        var files = await db.SigningData.Where(s => s.UserId == userId)
            .Select(s => s.PresignedPdfPath).ToListAsync(ct);
        counts["presigns_deleted"] = await db.SigningData.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);

        // Stored results (signed PDFs) of paid operations; the charge is in the usage records.
        counts["operations_deleted"] = await db.ApiOperations.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        counts["templates_deleted"] = await db.StoredPdfTemplates.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);

        var tokenIds = await db.ApiTokens.Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync(ct);
        counts["api_tokens_deleted"] = await db.ApiTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);

        // Usage records stay (billing history) but must not point at the documents going away.
        await db.UsageRecords
            .Where(r => r.DocumentId != null && db.Documents.Any(d => d.Id == r.DocumentId && d.UserId == userId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DocumentId, (Guid?)null), ct);
        counts["documents_deleted"] = await db.Documents.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);

        counts["consents_pseudonymized"] = await db.ConsentRecords
            .Where(c => c.UserId == userId && c.UserAgent != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UserAgent, (string?)null), ct);
        var consentsKept = await db.ConsentRecords.CountAsync(c => c.UserId == userId, ct);

        // Unsent messages about this account: never sent now, and their payloads (addresses,
        // ticket texts) dropped. The erasure's own items are left alone.
        counts["outbox_cancelled"] = await db.BackofficeOutboxItems
            .Where(i => (i.SubjectRef == subjectRef || i.SubjectRef == emailItemRef)
                        && !i.Kind.StartsWith(AccountErasure.KindPrefix)
                        && i.Status != BackofficeOutboxStatus.Sent
                        && i.Status != BackofficeOutboxStatus.FallbackSent
                        && i.Status != BackofficeOutboxStatus.Cancelled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, BackofficeOutboxStatus.Cancelled)
                .SetProperty(i => i.PayloadProtected, (string?)null)
                .SetProperty(i => i.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(i => i.LastError, "Cancelled: account erased (#217)"), ct);
        // Finished items keep no payload, but their ref still names the account.
        await db.BackofficeOutboxItems
            .Where(i => (i.SubjectRef == subjectRef || i.SubjectRef == emailItemRef)
                        && !i.Kind.StartsWith(AccountErasure.KindPrefix)
                        && i.PayloadProtected != null
                        && (i.Status == BackofficeOutboxStatus.Sent || i.Status == BackofficeOutboxStatus.FallbackSent))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.PayloadProtected, (string?)null), ct);

        // Processed events that name the account (its id, its own address as a whole JSON string).
        // Unprocessed ones are left to their handler (this one's own event among them).
        var quotedEmail = email is null ? null : "\"" + email.ToLowerInvariant() + "\"";
        counts["inbox_deleted"] = await db.BackofficeWebhookInboxItems
            .Where(i => i.ProcessedAt != null
                        && (i.PayloadJson.Contains(userIdText)
                            || (quotedEmail != null && i.PayloadJson.ToLower().Contains(quotedEmail))))
            .ExecuteDeleteAsync(ct);

        var quotedCustomer = stripeCustomerId is null ? null : "\"" + stripeCustomerId + "\"";
        counts["stripe_events_deleted"] = await db.WebhookEvents
            .Where(w => w.ProcessedAt != null
                        && (w.PayloadJson.Contains(userIdText)
                            || (quotedCustomer != null && w.PayloadJson.Contains(quotedCustomer))
                            || (quotedEmail != null && w.PayloadJson.ToLower().Contains(quotedEmail))))
            .ExecuteDeleteAsync(ct);

        // Sign-in, reset and re-auth attempt counters keyed by the address or the id.
        var emailKey = email is null ? null : ":" + UserEmails.Key(email);
        var idKey = ":" + userIdText;
        await db.AuthAttemptCounters
            .Where(c => c.Key.EndsWith(idKey) || (emailKey != null && c.Key.EndsWith(emailKey)))
            .ExecuteDeleteAsync(ct);

        var invoicesKept = await db.Invoices.CountAsync(i => i.UserId == userId, ct);
        var paymentsKept = await db.Payments.CountAsync(p => p.UserId == userId, ct);
        counts["invoices_kept"] = invoicesKept;
        counts["payments_kept"] = paymentsKept;
        counts["usage_records_kept"] = await db.UsageRecords.CountAsync(r => r.UserId == userId, ct);
        counts["consents_kept"] = consentsKept;
        counts["accounts_erased"] = firstTime ? 1 : 0;

        if (firstTime)
        {
            Tombstone(user, now);
            db.AdminAuditEntries.Add(new AdminAuditEntry
            {
                CreatedAt = now,
                // The account itself (or nobody, for a request through the service); never an address.
                ActorUserId = ack == null ? userId : Guid.Empty,
                ActorEmail = string.Empty,
                TargetUserId = userId,
                Action = AuditAction,
                Change = System.Text.Json.JsonSerializer.Serialize(new { origin, erasure_id = ack?.ErasureId, counts }),
            });
        }

        var retainedReason = invoicesKept + paymentsKept > 0
            ? AccountErasure.RetainedReasons.Accounting
            : consentsKept > 0 ? AccountErasure.RetainedReasons.ConsentEvidence : null;

        if (ack != null)
        {
            EnqueueAck(outbox, ack,
                retainedReason is null ? AccountErasure.AckStatuses.Done : AccountErasure.AckStatuses.Retained,
                AckCounts(counts), retainedReason, subjectRef);
        }
        else if (firstTime && backoffice.AnyEnabled)
        {
            // The service erases its own targets (consents, e-mail log, support, billing, …).
            outbox.Enqueue(AccountErasure.RequestOutboxKind, new ErasureRequestPayload
            {
                SubjectRef = subjectRef,
                Emails = email is null ? null : [email],
                CustomerRef = backoffice.ModeFor(BackofficeModule.Billing) == BackofficeMode.On && stripeCustomerId != null
                    ? BillingCustomerRef.For(userId)
                    : null,
                Origin = origin,
            }, critical: false, subjectRef: subjectRef);
        }
        else if (firstTime)
        {
            _logger.LogWarning(
                "Account erasure of user {UserId}: the backoffice integration is off; nothing is sent to the service", userId);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var status = firstTime ? AccountErasureStatus.Erased : AccountErasureStatus.AlreadyErased;
        return (new AccountErasureResult(status, counts, retainedReason), files, tokenIds);
    }

    /// <summary>Everything that identifies the person goes; the row stays for the invoices pointing at it.</summary>
    internal static void Tombstone(User user, DateTimeOffset now)
    {
        user.Email = AccountErasure.TombstoneEmail(user.Id);
        // Random hash and salt: no password matches (an empty hash would be a special case to get wrong).
        user.PasswordHash = RandomNumberGenerator.GetBytes(32);
        user.PasswordSalt = RandomNumberGenerator.GetBytes(16);
        user.StripeCustomerId = null;
        user.IsActive = false;
        user.IsAdmin = false;
        user.SealAllowed = false;
        user.EmailVerified = false;
        user.EmailVerificationToken = null;
        user.EmailVerificationExpiresAt = null;
        user.EmailOtpCode = null;
        user.EmailOtpExpiresAt = null;
        user.EmailOtpFailedAttempts = 0;
        user.LoginChallengeHash = null;
        user.PasswordResetToken = null;
        user.PasswordResetExpiresAt = null;
        user.AutoRechargeEnabled = false;
        user.AutoRechargeQuantity = 0;
        user.AutoRechargeCancelToken = null;
        user.PriceChangeNotifiedAt = null;
        user.PriceChangeNotifiedVersion = null;
        user.EmailNotificationsEnabled = false;
        user.EmailBouncedAt = null;
        user.Locale = null;
        user.ErasedAt = now;
        // UpdatedAt (the session stamp) moves with IsActive: every open session ends.
    }

    /// <summary>The ack's counts: at most 20 keys, values 0…10⁹ (contract).</summary>
    internal static IReadOnlyDictionary<string, int> AckCounts(IReadOnlyDictionary<string, int> counts) =>
        counts.Take(20).ToDictionary(c => c.Key, c => Math.Clamp(c.Value, 0, 1_000_000_000), StringComparer.Ordinal);

    private static void EnqueueAck(
        IBackofficeOutbox outbox, ErasureAckTarget ack, string status, IReadOnlyDictionary<string, int>? counts, string? retainedReason,
        string? subjectRef)
    {
        outbox.Enqueue(AccountErasure.AckOutboxKind, new ErasureAckPayload
        {
            ErasureId = ack.ErasureId,
            Product = ack.ProductSlug,
            Status = status,
            Counts = counts,
            RetainedReason = retainedReason,
        }, critical: false, subjectRef: subjectRef);
    }

    private void DeleteFile(string? path, Guid userId)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // PresignCleanupService removes orphaned presign files older than 30 minutes.
            _logger.LogWarning(ex, "Account erasure of user {UserId}: a presign temp file could not be deleted now; the presign sweep removes it", userId);
        }
    }
}
