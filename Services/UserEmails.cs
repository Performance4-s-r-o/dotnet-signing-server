using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DotNetSigningServer.Services;

/// <summary>
/// One account per address regardless of letter case (#82): <c>Foo@x.cz</c> and <c>foo@x.cz</c>
/// are the same mailbox in practice. Case is folded in one place only, PostgreSQL's
/// <c>lower()</c> on both sides of the comparison — the same function as the unique index
/// <c>IX_Users_Email_Lower</c>. .NET only trims: its <c>ToLowerInvariant</c> and PostgreSQL's
/// <c>lower()</c> (which follows <c>LC_CTYPE</c>) disagree outside ASCII, and a mix of the two
/// would make such an account impossible to find.
/// </summary>
public static class UserEmails
{
    /// <summary>Addresses under <c>.invalid</c> (RFC 2606) are reserved for service accounts; nobody signs up with one.</summary>
    public static bool IsReserved(string? email)
    {
        var at = Trim(email).LastIndexOf('@');
        if (at < 0) return false;
        var domain = Trim(email)[(at + 1)..];
        return domain.Equals("invalid", StringComparison.OrdinalIgnoreCase)
               || domain.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The form an address is stored and looked up in: trimmed, case kept.</summary>
    public static string Trim(string? email) => (email ?? "").Trim();

    /// <summary>
    /// In-process key of an address (rate-limit windows): trimmed and case-folded, so every
    /// spelling the lookup treats as one account shares one key. Never compared with the database.
    /// </summary>
    public static string Key(string? email) => Trim(email).ToLowerInvariant();

    /// <summary>
    /// The user with this address in any letter case, or null. Should two legacy rows differ
    /// only by case (possible before the unique index), only an exact match is returned —
    /// never a guess between two accounts.
    /// </summary>
    public static async Task<User?> FindByEmailAsync(
        this DbSet<User> users, string? email, CancellationToken cancellationToken = default)
    {
        var trimmed = Trim(email);
        if (trimmed.Length == 0) return null;

        var matches = await SameAddress(users, trimmed).ToListAsync(cancellationToken);
        if (matches.Count <= 1) return matches.FirstOrDefault();
        return matches.SingleOrDefault(u => string.Equals(u.Email, trimmed, StringComparison.Ordinal));
    }

    /// <summary>Whether any user has this address in any letter case.</summary>
    public static Task<bool> AnyWithEmailAsync(
        this DbSet<User> users, string? email, CancellationToken cancellationToken = default)
    {
        var trimmed = Trim(email);
        return trimmed.Length == 0 ? Task.FromResult(false) : SameAddress(users, trimmed).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// <c>lower("Email") = lower(@email)</c>, folded by the database on both sides. LINQ cannot
    /// say it: a lower-casing of the parameter would be evaluated in .NET before the query is sent.
    /// Other providers (tests) compare in memory.
    /// </summary>
    private static IQueryable<User> SameAddress(DbSet<User> users, string trimmed)
    {
        if (users.GetService<ICurrentDbContext>().Context.Database.IsNpgsql())
        {
            return users.FromSqlInterpolated(
                $"""SELECT * FROM dotnet_signing."Users" WHERE lower("Email") = lower({trimmed})""");
        }
        var folded = trimmed.ToLowerInvariant();
        return users.Where(u => u.Email.ToLower() == folded);
    }
}
