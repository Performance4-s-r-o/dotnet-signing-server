using System.Collections.Concurrent;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Fixed-window limiter for sign-in, 2FA, password reset and re-auth (#80), shared by every
/// instance: PostgreSQL holds the counters (<see cref="AuthAttemptCounter"/>), one atomic upsert
/// per attempt. Redis was the alternative; the database is already there and the traffic on
/// these forms is small.
///
/// Without PostgreSQL (unit tests on the in-memory provider) it counts in the process.
/// </summary>
public class AuthAttemptLimiter
{
    public const int DefaultMaxAttempts = 5;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> InProcess = new();

    private readonly ApplicationDbContext _db;

    public AuthAttemptLimiter(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>Counts one attempt for <paramref name="key"/>; true when it is over the limit.</summary>
    public async Task<bool> IsLimitedAsync(string key, int maxAttempts = DefaultMaxAttempts, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        var span = window ?? DefaultWindow;
        if (key.Length > 200) key = key[..200];
        var count = _db.Database.IsNpgsql()
            ? await HitDatabaseAsync(key, span, cancellationToken)
            : HitInProcess(key, span);
        return count > maxAttempts;
    }

    private async Task<int> HitDatabaseAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var entity = _db.Model.FindEntityType(typeof(AuthAttemptCounter))!;
        var table = $"\"{entity.GetSchema() ?? "dotnet_signing"}\".\"{entity.GetTableName()}\"";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - window;

        // Only the table name is spliced in, and it comes from the EF model, never from input;
        // every value goes as a parameter ({0}, {1}, {2}).
        // One statement: a new key starts at 1, an expired window restarts at 1, otherwise +1.
        var upsert =
            $@"INSERT INTO {table} AS c (""Key"", ""Count"", ""WindowStart"") VALUES ({{0}}, 1, {{1}})
               ON CONFLICT (""Key"") DO UPDATE SET
                   ""Count"" = CASE WHEN c.""WindowStart"" < {{2}} THEN 1 ELSE c.""Count"" + 1 END,
                   ""WindowStart"" = CASE WHEN c.""WindowStart"" < {{2}} THEN {{1}} ELSE c.""WindowStart"" END
               RETURNING c.""Count"" AS ""Value""";
        var count = await _db.Database.SqlQueryRaw<int>(upsert, key, now, cutoff).ToListAsync(cancellationToken);

        // Old windows are useless; clear them now and then instead of in a job.
        if (Random.Shared.Next(100) == 0)
        {
            var purge = $@"DELETE FROM {table} WHERE ""WindowStart"" < {{0}}";
            await _db.Database.ExecuteSqlRawAsync(purge, new object[] { now.AddDays(-1) }, cancellationToken);
        }

        return count.Single();
    }

    private static int HitInProcess(string key, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = InProcess.AddOrUpdate(
            key,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart > window ? (1, now) : (existing.Count + 1, existing.WindowStart));
        return entry.Count;
    }
}
