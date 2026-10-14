using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Billing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Operations;

/// <summary>What a paid call asks for: who, which endpoint, under which key, at what price.</summary>
/// <param name="ClientOperationId">The caller's <c>Idempotency-Key</c>, already validated; null when none was sent.</param>
/// <param name="Fingerprint">Identity of the request (<see cref="ApiOperationService.Fingerprint"/>).</param>
/// <param name="Tier">Concurrency tier the price is multiplied by.</param>
/// <param name="PresignId"><c>/api/sign</c>: the presign consumed. It keys the operation even without a header.</param>
public sealed record OperationRequest(
    Guid UserId,
    bool Enterprise,
    string Operation,
    string? ClientOperationId,
    string Fingerprint,
    int BaseCost,
    int Tier,
    string? PresignId = null);

public enum OperationStart
{
    /// <summary>Credits reserved, the caller does the work now.</summary>
    Started,
    /// <summary>Already completed; hand back the stored result, charge nothing.</summary>
    Replay,
    /// <summary>Another attempt holds the lease.</summary>
    InProgress,
    /// <summary>The key belongs to a different request (endpoint, body or presign).</summary>
    KeyReused,
    /// <summary>The reservation could not be made; nothing ran.</summary>
    InsufficientCredits,
    /// <summary>Completed and charged, but the stored result has already been purged.</summary>
    ResultExpired,
}

/// <param name="Attempt">The attempt this caller owns; every later transition is conditional on it.</param>
public sealed record OperationBegin(OperationStart Outcome, ApiOperation Operation, int Attempt = 0);

/// <summary>A response body kept for replays.</summary>
/// <param name="Json">The body exactly as the first answer serialised it.</param>
/// <param name="IsPdf">The body's <c>result</c> is the PDF, so a raw <c>application/pdf</c> answer can be rebuilt.</param>
public sealed record StoredResult(int StatusCode, string Json, bool IsPdf, IReadOnlyDictionary<string, string>? PdfHeaders);

/// <summary>
/// Durable state of paid API operations (#77) and the reservation of their credits (#82).
///
/// <list type="number">
/// <item><b>Begin</b> finds or creates the row for the key and, in one transaction, moves it to
/// <c>running</c> and takes the price from the balance (the reservation). No credits, no work.</item>
/// <item><b>Complete</b> stores the result, marks it <c>completed</c> and records usage, in one
/// transaction — the reservation becomes the charge.</item>
/// <item><b>Fail</b> marks <c>failed</c> or <c>failed_uncertain</c> and returns the reservation.</item>
/// </list>
///
/// Every transition is a conditional UPDATE on (state, attempt), so two requests with one key
/// cannot both start, and an attempt that lost its lease cannot complete or refund twice.
/// </summary>
public class ApiOperationService
{
    /// <summary>A running attempt not finished by then is presumed dead and its credits come back.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a completed result can be fetched again. The portal holds a reservation 15 min
    /// (utilities) or 35 min (two-step signature) and retries within that.
    /// </summary>
    public static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(60);

    /// <summary>How long the state stays queryable — the portal's reconciliation asks after its TTL.</summary>
    public static readonly TimeSpan RowRetention = TimeSpan.FromDays(7);

    public const int MaxOperationIdLength = 128;

    public const string InsufficientCreditsCode = "INSUFFICIENT_CREDITS";
    public const string LeaseExpiredCode = "LEASE_EXPIRED";

    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<ApiOperationService> _logger;

    public ApiOperationService(ApplicationDbContext db, IDataProtectionProvider dataProtection, ILogger<ApiOperationService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("ApiOperations.Result");
        _logger = logger;
    }

    /// <summary>
    /// A key the portal can send: a UUID in practice; anything printable from a safe alphabet,
    /// at most <see cref="MaxOperationIdLength"/> characters.
    /// </summary>
    public static bool IsValidOperationId(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxOperationIdLength) return false;
        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return false;
        }
        return true;
    }

    /// <summary>SHA-256 over the JSON of the parts — the same request always hashes the same.</summary>
    public static string Fingerprint(params object?[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(part == null ? "null"u8 : JsonSerializer.SerializeToUtf8Bytes(part, part.GetType()));
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public Task<ApiOperation?> FindAsync(Guid userId, string operationId, CancellationToken ct = default) =>
        _db.ApiOperations.AsNoTracking().SingleOrDefaultAsync(o => o.UserId == userId && o.OperationId == operationId, ct);

    public Task<ApiOperation?> FindByPresignAsync(string presignId, CancellationToken ct = default) =>
        _db.ApiOperations.AsNoTracking().SingleOrDefaultAsync(o => o.PresignId == presignId, ct);

    public async Task<OperationBegin> BeginAsync(OperationRequest request, CancellationToken ct = default)
    {
        // A few rounds: a concurrent insert, an expired lease or a lost claim each send us
        // back to re-read the row. More than that means something is wrong; report busy.
        ApiOperation? op = null;
        for (var round = 0; round < 4; round++)
        {
            var (found, conflict) = await FindOrCreateAsync(request, ct);
            if (conflict != null) return new OperationBegin(OperationStart.KeyReused, conflict);
            if (found == null) continue;
            op = found;

            if (op.Operation != request.Operation || op.RequestFingerprint != request.Fingerprint)
            {
                return new OperationBegin(OperationStart.KeyReused, op);
            }

            var now = DateTimeOffset.UtcNow;
            if (op.State == ApiOperationState.Running && op.LeaseUntil < now)
            {
                await FailAsync(op, op.Attempts, uncertain: true, LeaseExpiredCode);
                continue;
            }

            switch (op.State)
            {
                case ApiOperationState.Completed:
                    return new OperationBegin(
                        op.ResultProtected != null ? OperationStart.Replay : OperationStart.ResultExpired, op);
                case ApiOperationState.Running:
                    return new OperationBegin(OperationStart.InProgress, op);
            }

            var started = await TryClaimAsync(op, request, ct);
            if (started != null) return started;
        }

        return new OperationBegin(OperationStart.InProgress, op ?? new ApiOperation
        {
            UserId = request.UserId,
            OperationId = request.ClientOperationId ?? string.Empty,
            Operation = request.Operation,
            State = ApiOperationState.Running,
        });
    }

    private async Task<(ApiOperation? Found, ApiOperation? Conflict)> FindOrCreateAsync(OperationRequest request, CancellationToken ct)
    {
        ApiOperation? op = null;
        if (request.PresignId != null)
        {
            op = await FindByPresignAsync(request.PresignId, ct);
            // The presign decides which operation this is; a different key for it is a
            // different request.
            if (op != null && (op.UserId != request.UserId
                               || (request.ClientOperationId != null && op.ClientKey && op.OperationId != request.ClientOperationId)))
            {
                return (null, op);
            }
        }

        if (op == null && request.ClientOperationId != null)
        {
            op = await FindAsync(request.UserId, request.ClientOperationId, ct);
            // Same key, other presign: the fingerprint check refuses it.
        }

        if (op != null) return (op, null);

        var now = DateTimeOffset.UtcNow;
        var row = new ApiOperation
        {
            UserId = request.UserId,
            OperationId = request.ClientOperationId ?? Guid.NewGuid().ToString(),
            ClientKey = request.ClientOperationId != null,
            Operation = request.Operation,
            RequestFingerprint = request.Fingerprint,
            PresignId = request.PresignId,
            State = ApiOperationState.Pending,
            BaseCost = request.BaseCost,
            Tier = request.Tier,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ApiOperations.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return (row, null);
        }
        catch (DbUpdateException ex) when (CreditGrants.IsUniqueViolation(ex))
        {
            // Created concurrently by the same key or presign; read theirs on the next round.
            return (null, null);
        }
        finally
        {
            _db.Entry(row).State = EntityState.Detached;
        }
    }

    private Task<OperationBegin?> TryClaimAsync(ApiOperation op, OperationRequest request, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async token =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(token);
            var now = DateTimeOffset.UtcNow;
            var leaseUntil = now + Lease;
            var seen = op.Attempts;

            // The row lock taken here serialises a concurrent claim of the same key: the
            // other UPDATE waits for this commit, re-reads the state and matches nothing.
            var claimed = await _db.ApiOperations
                .Where(o => o.Id == op.Id
                            && o.Attempts == seen
                            && (o.State == ApiOperationState.Pending
                                || o.State == ApiOperationState.Failed
                                || o.State == ApiOperationState.FailedUncertain))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.State, ApiOperationState.Running)
                    .SetProperty(o => o.Attempts, o => o.Attempts + 1)
                    .SetProperty(o => o.LeaseUntil, leaseUntil)
                    .SetProperty(o => o.BaseCost, request.BaseCost)
                    .SetProperty(o => o.Tier, request.Tier)
                    .SetProperty(o => o.CreditsReserved, 0)
                    .SetProperty(o => o.ErrorCode, (string?)null)
                    .SetProperty(o => o.UpdatedAt, now), token);
            if (claimed == 0) return null;

            var price = request.BaseCost * request.Tier;
            var reserved = 0;
            // Enterprise accounts are billed from usage and never decremented.
            if (!request.Enterprise && price > 0)
            {
                var debited = await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" - {0} WHERE \"Id\" = {1} AND \"CreditsRemaining\" >= {2}",
                    [price, request.UserId, price], token);
                if (debited == 0)
                {
                    await _db.ApiOperations
                        .Where(o => o.Id == op.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(o => o.State, ApiOperationState.Failed)
                            .SetProperty(o => o.ErrorCode, InsufficientCreditsCode)
                            .SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null), token);
                    await tx.CommitAsync(token);
                    return new OperationBegin(OperationStart.InsufficientCredits, Snapshot(op, ApiOperationState.Failed, seen + 1, 0, request));
                }

                reserved = price;
                await _db.ApiOperations
                    .Where(o => o.Id == op.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreditsReserved, reserved), token);
            }

            await tx.CommitAsync(token);
            var started = Snapshot(op, ApiOperationState.Running, seen + 1, reserved, request);
            started.LeaseUntil = leaseUntil;
            return new OperationBegin(OperationStart.Started, started, seen + 1);
        }, ct)!;
    }

    private static ApiOperation Snapshot(ApiOperation op, string state, int attempts, int reserved, OperationRequest request) => new()
    {
        Id = op.Id,
        UserId = op.UserId,
        OperationId = op.OperationId,
        ClientKey = op.ClientKey,
        Operation = op.Operation,
        RequestFingerprint = op.RequestFingerprint,
        PresignId = op.PresignId,
        State = state,
        Attempts = attempts,
        BaseCost = request.BaseCost,
        Tier = request.Tier,
        CreditsReserved = reserved,
        ErrorCode = state == ApiOperationState.Failed ? InsufficientCreditsCode : null,
        CreatedAt = op.CreatedAt,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Stores the result and turns the reservation into the charge, in one transaction.
    /// False when this attempt no longer owns the operation (its lease expired and the credits
    /// went back) — then the result must not be delivered.
    /// </summary>
    public Task<bool> CompleteAsync(ApiOperation op, int attempt, StoredResult result)
    {
        var body = _protector.Protect(Encoding.UTF8.GetBytes(result.Json));
        var headers = result.PdfHeaders is { Count: > 0 } ? JsonSerializer.Serialize(result.PdfHeaders) : null;
        var charged = op.BaseCost * op.Tier;
        UsageRecord? usage = null;

        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async token =>
        {
            // A retried attempt starts from the database, not from the failed one.
            if (usage != null) _db.Entry(usage).State = EntityState.Detached;
            usage = null;

            await using var tx = await _db.Database.BeginTransactionAsync(token);
            var now = DateTimeOffset.UtcNow;
            var done = await _db.ApiOperations
                .Where(o => o.Id == op.Id && o.State == ApiOperationState.Running && o.Attempts == attempt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.State, ApiOperationState.Completed)
                    .SetProperty(o => o.CreditsCharged, charged)
                    .SetProperty(o => o.CreditsReserved, 0)
                    .SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(o => o.ErrorCode, (string?)null)
                    .SetProperty(o => o.ResultStatusCode, result.StatusCode)
                    .SetProperty(o => o.ResultIsPdf, result.IsPdf)
                    .SetProperty(o => o.ResultProtected, body)
                    .SetProperty(o => o.ResultPdfHeaders, headers)
                    .SetProperty(o => o.ResultExpiresAt, now + ResultRetention)
                    .SetProperty(o => o.CompletedAt, now)
                    .SetProperty(o => o.UpdatedAt, now), token);
            if (done == 0) return false;

            if (charged > 0)
            {
                usage = new UsageRecord
                {
                    UserId = op.UserId,
                    Count = charged,
                    BaseCost = op.BaseCost,
                    Tier = op.Tier,
                    Operation = op.Operation,
                    CreatedAt = now,
                };
                _db.UsageRecords.Add(usage);
                await _db.SaveChangesAsync(token);
                _db.Entry(usage).State = EntityState.Detached;
            }

            await tx.CommitAsync(token);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Ends the attempt as failed and returns its reservation. Does nothing when the attempt
    /// is no longer the running one (already completed, failed or expired) — so a reservation
    /// is never returned twice.
    /// </summary>
    public Task<bool> FailAsync(ApiOperation op, int attempt, bool uncertain, string errorCode)
    {
        var state = uncertain ? ApiOperationState.FailedUncertain : ApiOperationState.Failed;
        var code = errorCode.Length > 64 ? errorCode[..64] : errorCode;
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async token =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(token);
            // Read the reservation under the row lock; the snapshot the caller holds may be older.
            var reserved = await _db.ApiOperations
                .Where(o => o.Id == op.Id && o.State == ApiOperationState.Running && o.Attempts == attempt)
                .Select(o => (int?)o.CreditsReserved)
                .FirstOrDefaultAsync(token);
            var ended = await _db.ApiOperations
                .Where(o => o.Id == op.Id && o.State == ApiOperationState.Running && o.Attempts == attempt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.State, state)
                    .SetProperty(o => o.ErrorCode, code)
                    .SetProperty(o => o.CreditsReserved, 0)
                    .SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(o => o.UpdatedAt, DateTimeOffset.UtcNow), token);
            if (ended == 0) return false;

            if (reserved is > 0)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
                    [reserved.Value, op.UserId], token);
            }

            await tx.CommitAsync(token);
            return true;
        }, CancellationToken.None);
    }

    public StoredResult ReadResult(ApiOperation op)
    {
        if (op.ResultProtected == null)
        {
            throw new InvalidOperationException($"Operation {op.Id} has no stored result.");
        }
        var json = Encoding.UTF8.GetString(_protector.Unprotect(op.ResultProtected));
        var headers = string.IsNullOrEmpty(op.ResultPdfHeaders)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(op.ResultPdfHeaders);
        return new StoredResult(op.ResultStatusCode ?? StatusCodes.Status200OK, json, op.ResultIsPdf, headers);
    }

    /// <summary>
    /// Returns the credits of attempts that died (lease expired), purges stored results past
    /// their retention and deletes old rows. Returns how many leases were expired.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var stale = await _db.ApiOperations.AsNoTracking()
            .Where(o => o.State == ApiOperationState.Running && o.LeaseUntil < now)
            .OrderBy(o => o.LeaseUntil)
            .Take(200)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var op in stale)
        {
            if (await FailAsync(op, op.Attempts, uncertain: true, LeaseExpiredCode))
            {
                expired++;
                _logger.LogWarning(
                    "Operation {OperationId} ({Operation}) lost its lease; {Credits} reserved credit(s) returned",
                    op.OperationId, op.Operation, op.CreditsReserved);
            }
        }

        await _db.ApiOperations
            .Where(o => o.ResultProtected != null && o.ResultExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ResultProtected, (byte[]?)null), ct);

        var cutoff = now - RowRetention;
        await _db.ApiOperations
            .Where(o => o.UpdatedAt < cutoff && o.State != ApiOperationState.Running)
            .ExecuteDeleteAsync(ct);

        return expired;
    }
}
