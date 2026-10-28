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

    /// <summary>
    /// Not erased on purpose (<see cref="AccountErasureResult.Refusal"/>): nothing was erased and
    /// nothing sent. The account page shows why; a request through the service acks
    /// <c>failed</c>, logs an error and is retried.
    /// </summary>
    Refused,
}

/// <summary>Why an account is not erased.</summary>
public enum AccountErasureRefusal
{
    None,

    /// <summary>The portal's service account (<c>TRUSTED_CALLER_USER_IDS</c>): every tenant's signing runs on it.</summary>
    PortalAccount,

    /// <summary>The only active administrator: nobody could manage the installation afterwards.</summary>
    LastAdmin,

    /// <summary>An auto-recharge charge of the account is running or settling; try again later.</summary>
    RechargeInProgress,
}

/// <param name="Counts">What happened, for logs and the ack: no personal data.</param>
/// <param name="RetainedReason">The reason the ack carries (<see cref="AccountErasure.RetainedReasons"/>); null = nothing kept.</param>
/// <param name="RetainedReasons">Every reason that applies, most specific first (the ack carries only one).</param>
/// <param name="Refusal">Why the account was not erased (<see cref="AccountErasureStatus.Refused"/>).</param>
/// <param name="ChargesStopped">Auto-recharge was switched off and saved cards removed before the step that failed.</param>
public sealed record AccountErasureResult(
    AccountErasureStatus Status,
    IReadOnlyDictionary<string, int> Counts,
    string? RetainedReason,
    IReadOnlyList<string>? RetainedReasons = null,
    AccountErasureRefusal Refusal = AccountErasureRefusal.None,
    bool ChargesStopped = false)
{
    public bool Succeeded => Status is AccountErasureStatus.Erased or AccountErasureStatus.AlreadyErased or AccountErasureStatus.NotFound;

    internal static AccountErasureResult Refused(AccountErasureRefusal refusal, bool chargesStopped = false) =>
        new(AccountErasureStatus.Refused, new Dictionary<string, int>(), null, null, refusal, chargesStopped);
}

/// <summary>An erasure asked for by the service (<c>subject.erasure.requested</c>): ack its product target.</summary>
public sealed record ErasureAckTarget(Guid ErasureId, string ProductSlug);

/// <summary>
/// Erases one account (GDPR art. 17, #217) — from the account page (<c>account_deleted</c>) or for
/// a request the service received (<c>subject_request</c>).
///
/// <list type="number">
/// <item>Refused (<see cref="AccountErasureStatus.Refused"/>) for the portal's service account,
/// the last active administrator, and while an auto-recharge charge runs or settles.</item>
/// <item>Auto-recharge is switched off (own commit), then saved cards are removed at the payment
/// provider (this product has no Stripe subscription; auto-recharge on a saved card is its only
/// recurring charge). Failure = <see cref="AccountErasureStatus.BillingFailed"/>: nothing is
/// erased, nothing sent — but auto-recharge stays off and some cards may be gone.</item>
/// <item>One transaction, the user row locked (<c>FOR UPDATE</c>), so two erasures of the same
/// account run one after the other and the second finds a tombstone:
/// presigns, API operations, stored templates, API tokens and documents are deleted; usage
/// records stay (on the tombstone, billing history); consent records lose the user agent
/// (append-only otherwise, proof of consent); the subject's unsent outbox items are cancelled
/// and their payloads dropped; processed inbox rows naming the account are deleted and the
/// account's Stripe events lose their payload (the row stays, its id deduplicates); sign-in attempt counters go; the user row becomes a tombstone without personal data
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

        var backoffice = services.GetRequiredService<IOptions<P4BackofficeProductOptions>>().Value;
        var chargesStopped = false;
        if (existing.ErasedAt == null)
        {
            var refusal = await RefusalAsync(services, db, existing, cancellationToken);
            if (refusal != AccountErasureRefusal.None)
            {
                LogRefusal(origin, userId, refusal);
                return AccountErasureResult.Refused(refusal);
            }

            // Own commit, before the cards: a charge starting from now on finds it off.
            await db.Users.Where(u => u.Id == userId && u.AutoRechargeEnabled)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.AutoRechargeEnabled, false), cancellationToken);
            chargesStopped = true;
            var stopped = await StopRecurringChargesAsync(services, existing, cancellationToken);
            if (!stopped)
            {
                return new AccountErasureResult(AccountErasureStatus.BillingFailed, new Dictionary<string, int>(), null, ChargesStopped: true);
            }
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var (result, files, tokenIds) = await strategy.ExecuteAsync(
            ct => EraseLocallyAsync(services, db, userId, origin, ack, backoffice, ct), cancellationToken);
        if (result.Status == AccountErasureStatus.Refused)
        {
            LogRefusal(origin, userId, result.Refusal);
            return result with { ChargesStopped = chargesStopped };
        }

        foreach (var path in files) DeleteFile(path, userId);
        var apiAuth = services.GetService<IApiAuthService>();
        foreach (var tokenId in tokenIds) apiAuth?.InvalidateTokenCache(tokenId);

        _logger.LogInformation(
            "Account erasure ({Origin}) of user {UserId}: {Status}, {Counts}, retained {RetainedReason}",
            origin, userId, result.Status, string.Join(", ", result.Counts.Select(c => $"{c.Key}={c.Value}")), result.RetainedReason ?? "nothing");
        return result;
    }

    /// <summary>
    /// Why the account must not be erased now; checked before anything changes and again under
    /// the lock inside the transaction.
    /// </summary>
    private static async Task<AccountErasureRefusal> RefusalAsync(
        IServiceProvider services, ApplicationDbContext db, User user, CancellationToken cancellationToken)
    {
        var portal = services.GetService<PortalTrustOptions>();
        if (portal?.TrustedCallerUserIds.Contains(user.Id) == true) return AccountErasureRefusal.PortalAccount;

        if (user.IsAdmin && !await db.Users.AnyAsync(
                u => u.Id != user.Id && u.IsAdmin && u.IsActive && u.ErasedAt == null, cancellationToken))
        {
            return AccountErasureRefusal.LastAdmin;
        }

        // In this process, or in the shared claim (every replica).
        var claimId = AutoRechargeClaim.EventIdFor(user.Id);
        if (AutoRechargeService.IsInFlight(user.Id)
            || await db.WebhookEvents.AnyAsync(w => w.EventId == claimId, cancellationToken))
        {
            return AccountErasureRefusal.RechargeInProgress;
        }
        return AccountErasureRefusal.None;
    }

    private void LogRefusal(string origin, Guid userId, AccountErasureRefusal refusal)
    {
        if (refusal == AccountErasureRefusal.RechargeInProgress)
        {
            _logger.LogWarning("Account erasure ({Origin}) of user {UserId} postponed: an auto-recharge charge is running", origin, userId);
        }
        else
        {
            _logger.LogError("Account erasure ({Origin}) of user {UserId} refused: {Refusal}; a person has to decide", origin, userId, refusal);
        }
    }

    /// <summary>
    /// The payment provider's side, before anything local (the tombstone forgets the Stripe
    /// customer, so this must succeed first; every step can be repeated after a partial failure).
    /// The saved cards go through the billing API and are checked gone, so no off-session charge
    /// (auto-recharge) can follow; the billing service scrubs the customer itself
    /// (<c>customer_ref</c> of the erasure request). A user the service does not know answers 404, which is nothing to remove.
    /// </summary>
    private async Task<bool> StopRecurringChargesAsync(
        IServiceProvider services, User user, CancellationToken cancellationToken)
    {
        // No test on the local Stripe customer id: the service knows the payer by customer_ref
        // (the user's id), whether or not this product ever stored a link.
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: a provider's message may quote the customer's address.
            _logger.LogError("Account erasure of user {UserId}: the payment provider step failed ({Error}); the account is not erased",
                user.Id, ex.GetType().Name);
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
            // Serialises erasures of one account; the loser re-reads a tombstone below. The
            // administrators are locked too (in id order, so two admins erasing at once wait for
            // each other instead of deadlocking): the "last admin" check below must see the truth.
            var lockSql = $"SELECT 1 FROM {users.Table} WHERE {users[nameof(User.Id)]} = {{0}} OR {users[nameof(User.IsAdmin)]} "
                          + $"ORDER BY {users[nameof(User.Id)]} FOR UPDATE"; // names from the model
            await db.Database.ExecuteSqlRawAsync(lockSql, [userId], ct);
            // Lets the ConsentRecords trigger accept clearing UserAgent, in this transaction only.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL dotnet_signing.erasure = 'on'", ct);
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var firstTime = user.ErasedAt == null;
        if (firstTime)
        {
            var refusal = await RefusalAsync(services, db, user, ct);
            if (refusal != AccountErasureRefusal.None)
            {
                return (AccountErasureResult.Refused(refusal), [], []); // rolled back
            }
        }
        var email = firstTime ? UserEmails.Trim(user.Email) : null;
        var stripeCustomerId = string.IsNullOrWhiteSpace(user.StripeCustomerId) ? null : user.StripeCustomerId;
        var userIdText = userId.ToString("D");
        var subjectRef = AccountErasure.SubjectRef(userId);
        var emailItemRef = "user:" + userIdText; // e-mail outbox items (TemplatedEmailSender)
        // Refs below the subject (support tickets: dotnet:user:{id}/support/{key}). The "/" keeps
        // another id that merely starts with this one out.
        var subjectRefChild = subjectRef + "/";
        var emailItemRefChild = emailItemRef + "/";
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
            .Where(i => (i.SubjectRef == subjectRef || i.SubjectRef!.StartsWith(subjectRefChild)
                         || i.SubjectRef == emailItemRef || i.SubjectRef.StartsWith(emailItemRefChild))
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
            .Where(i => (i.SubjectRef == subjectRef || i.SubjectRef!.StartsWith(subjectRefChild)
                         || i.SubjectRef == emailItemRef || i.SubjectRef.StartsWith(emailItemRefChild))
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

        counts["stripe_events_scrubbed"] = await ScrubStripeEventsAsync(db, userIdText, stripeCustomerId, ct);

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
        // What this account did as an administrator stays (its trail must not be tidied away by
        // the people it records); those rows carry the address it had then.
        var auditRowsKept = await db.AdminAuditEntries.CountAsync(
            a => a.ActorUserId == userId && a.Action != AuditAction && a.ActorEmail != "", ct);
        counts["audit_rows_kept"] = auditRowsKept;

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

        var retainedReasons = new List<string>();
        if (auditRowsKept > 0) retainedReasons.Add(AccountErasure.RetainedReasons.LegalClaims);
        if (invoicesKept + paymentsKept > 0) retainedReasons.Add(AccountErasure.RetainedReasons.Accounting);
        if (consentsKept > 0) retainedReasons.Add(AccountErasure.RetainedReasons.ConsentEvidence);
        var retainedReason = retainedReasons.FirstOrDefault();

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
                CustomerRef = backoffice.ModeFor(BackofficeModule.Billing) == BackofficeMode.On
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
        return (new AccountErasureResult(status, counts, retainedReason, retainedReasons), files, tokenIds);
    }

    /// <summary>
    /// The account's Stripe events (<c>evt_…</c>) keep their row — the id deduplicates Stripe's
    /// redeliveries — but lose the payload. An event is the account's only when a JSON field says
    /// so: <c>customer</c> is its Stripe customer, or <c>userId</c> / <c>app_user_id</c> its id.
    /// A receipt address or any other text that happens to match decides nothing. The JSON is read
    /// token by token, so a payload cut at 4000 characters is still read up to the cut. Claims of
    /// auto-recharge, refusals and grant markers have other ids and are never touched.
    /// </summary>
    private static async Task<int> ScrubStripeEventsAsync(ApplicationDbContext db, string userIdText, string? stripeCustomerId, CancellationToken ct)
    {
        // Narrowed in SQL, decided by the parser.
        var candidates = await db.WebhookEvents
            .Where(w => w.EventId.StartsWith("evt_") && w.PayloadJson != "{}"
                        && (w.PayloadJson.Contains(userIdText) || (stripeCustomerId != null && w.PayloadJson.Contains(stripeCustomerId))))
            .ToListAsync(ct);
        var scrubbed = 0;
        foreach (var row in candidates)
        {
            if (!NamesAccount(row.PayloadJson, userIdText, stripeCustomerId)) continue;
            row.PayloadJson = "{}";
            scrubbed++;
        }
        return scrubbed;
    }

    /// <summary>Whether a Stripe payload's <c>customer</c>, <c>userId</c> or <c>app_user_id</c> names the account. Pure.</summary>
    internal static bool NamesAccount(string json, string userIdText, string? stripeCustomerId)
    {
        var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json), isFinalBlock: false, state: default);
        string? property = null;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case System.Text.Json.JsonTokenType.PropertyName:
                        property = reader.GetString();
                        continue;
                    case System.Text.Json.JsonTokenType.String when property is not null:
                        var value = reader.GetString();
                        if (stripeCustomerId != null && property == "customer" && value == stripeCustomerId) return true;
                        if (property is "userId" or "app_user_id"
                            && string.Equals(value, userIdText, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                }
                property = null;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Cut short or not JSON: what was read decides.
        }
        return false;
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
