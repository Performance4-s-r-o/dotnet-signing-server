using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice;

/// <summary>Reads and writes <see cref="BackofficeState"/> rows (cursor, snapshots).</summary>
public static class BackofficeStateStore
{
    public static async Task<string?> GetAsync(ApplicationDbContext db, string key, CancellationToken cancellationToken = default) =>
        await db.BackofficeStates.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Inserts or replaces the value and saves.</summary>
    public static async Task SetAsync(ApplicationDbContext db, string key, string value, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var row = await db.BackofficeStates.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row == null)
        {
            db.BackofficeStates.Add(new BackofficeState { Key = key, Value = value, UpdatedAt = now });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
