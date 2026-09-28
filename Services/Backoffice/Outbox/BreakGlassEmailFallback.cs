using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Break-glass for critical e-mail (2FA code, password reset, e-mail verification) while
/// <c>Modules:Email</c> is On: a critical <c>email.raw</c> item goes out directly through
/// <see cref="ResendEmailSender"/> when
/// <list type="bullet">
/// <item>it is still Pending <see cref="P4BackofficeProductOptions.EmailOptions.FallbackAfter"/>
/// (default 60 s) after it was queued ("silent" outage, timeouts), or</item>
/// <item>the service is known to be down (<see cref="OutboxCircuitBreaker"/>), or its last
/// attempt got no connection or a 5xx — then within one dispatcher pass, or</item>
/// <item>it is Blocked (the API key was refused).</item>
/// </list>
/// The item becomes <c>FallbackSent</c> with its payload cleared and is never sent by the
/// service afterwards (a code would have expired by then anyway). Items older than
/// <see cref="MaxAge"/> are left alone: whatever they carried has expired.
/// Disabled by <c>P4Backoffice:Email:FallbackDirect=false</c> and while Resend is not configured.
/// </summary>
public sealed class BreakGlassEmailFallback : IOutboxFallback
{
    /// <summary>Critical items older than this are not sent any more (the longest link, verification, lasts 24 h).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>After a failed direct send the item is left alone this long (also by the dispatcher).</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30);

    /// <summary>Items per run; critical mail is rare, and each send may take up to Resend's timeout.</summary>
    public const int BatchSize = 10;

    /// <summary>Remaining claimed items are released after this (well inside the claim's lease).</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxPayloadProtector _protector;
    private readonly OutboxCircuitBreaker _breaker;
    private readonly IOptionsMonitor<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BreakGlassEmailFallback> _logger;
    private bool _warnedNotConfigured;

    public BreakGlassEmailFallback(
        IServiceScopeFactory scopes,
        OutboxPayloadProtector protector,
        OutboxCircuitBreaker breaker,
        IOptionsMonitor<P4BackofficeProductOptions> options,
        TimeProvider time,
        ILogger<BreakGlassEmailFallback> logger)
    {
        _scopes = scopes;
        _protector = protector;
        _breaker = breaker;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var email = _options.CurrentValue.Email;
        if (!email.FallbackDirect) return 0;

        using var scope = _scopes.CreateScope();
        var resend = scope.ServiceProvider.GetRequiredService<ResendEmailSender>();
        if (!resend.IsConfigured)
        {
            if (!_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                _logger.LogWarning(
                    "Backoffice e-mail break-glass is unavailable: Resend is not configured (RESEND_API_KEY, EMAIL_FROM)");
            }
            return 0;
        }

        var now = _time.GetUtcNow();
        var serviceDown = _breaker.IsServiceDown;
        var pendingCreatedBefore = serviceDown ? now : now - email.FallbackAfter;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var items = await OutboxClaim.ClaimForFallbackAsync(
            db, BackofficeOutboxEmailSender.OutboxKind, now, pendingCreatedBefore, now - MaxAge, BatchSize, cancellationToken);
        if (items.Count == 0) return 0;

        var started = Stopwatch.StartNew();
        var finished = 0;
        foreach (var item in items)
        {
            if (started.Elapsed > RunBudget)
            {
                item.LockedUntil = null;
                continue;
            }
            if (await SendAsync(item, resend, Reason(item, now, serviceDown), cancellationToken)) finished++;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return finished;
    }

    private static string Reason(BackofficeOutboxItem item, DateTimeOffset now, bool serviceDown) =>
        item.Status == BackofficeOutboxStatus.Blocked ? "blocked (API key refused)"
        : serviceDown ? "service down"
        : OutboxClaim.LastAttemptFoundServiceDown(item.LastError) ? "service unreachable"
        : $"not delivered within {(now - item.CreatedAt).TotalSeconds:0} s";

    /// <summary>One direct send on a claimed item; records the outcome on it (not saved). True when finished.</summary>
    private async Task<bool> SendAsync(
        BackofficeOutboxItem item, ResendEmailSender resend, string reason, CancellationToken cancellationToken)
    {
        EmailRawPayload? payload;
        try
        {
            payload = item.PayloadProtected == null
                ? null
                : JsonSerializer.Deserialize<EmailRawPayload>(_protector.Unprotect(item.PayloadProtected), BackofficeOutbox.PayloadJson);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            payload = null;
        }
        if (payload == null || string.IsNullOrWhiteSpace(payload.To))
        {
            // Nothing that could be sent, by anyone.
            item.Status = BackofficeOutboxStatus.Dead;
            item.LockedUntil = null;
            item.LastError = "Break-glass: payload missing or unreadable";
            _logger.LogError("Backoffice e-mail {ItemId} cannot be sent by break-glass: payload missing or unreadable", item.Id);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await resend.SendAsync(payload.To, payload.Subject, payload.Html);
        }
        catch (Exception ex)
        {
            item.LockedUntil = _time.GetUtcNow() + RetryPause;
            item.LastError = RetryPolicy.Truncate($"Break-glass failed: {ex.GetType().Name}", RetryPolicy.MaxErrorLength);
            _logger.LogError(ex, "Backoffice e-mail {ItemId} break-glass send failed ({Reason}); retrying in {Pause}",
                item.Id, reason, RetryPause);
            return false;
        }

        item.Status = BackofficeOutboxStatus.FallbackSent;
        item.SentAt = _time.GetUtcNow();
        item.PayloadProtected = null;
        item.LockedUntil = null;
        item.LastError = RetryPolicy.Truncate("Break-glass: " + reason, RetryPolicy.MaxErrorLength);
        _logger.LogWarning(
            "Backoffice e-mail {ItemId} ({SubjectRef}) sent directly through Resend (break-glass): {Reason}",
            item.Id, item.SubjectRef, reason);
        return true;
    }
}
