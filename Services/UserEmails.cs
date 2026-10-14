using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// One account per address regardless of letter case (#82): <c>Foo@x.cz</c> and <c>foo@x.cz</c>
/// are the same mailbox in practice. New addresses are stored normalised; lookups compare
/// <c>lower("Email")</c>, which the unique index <c>IX_Users_Email_Lower</c> backs, so accounts
/// created before with mixed case are still found.
/// </summary>
public static class UserEmails
{
    /// <summary>The form an address is stored and compared in: trimmed, lower case.</summary>
    public static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// The user with this address in any letter case, or null. Should two legacy rows differ
    /// only by case (possible before the unique index), only an exact match is returned —
    /// never a guess between two accounts.
    /// </summary>
    public static async Task<User?> FindByEmailAsync(
        this IQueryable<User> users, string? email, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(email);
        if (normalized.Length == 0) return null;

        var matches = await users.Where(u => u.Email.ToLower() == normalized).ToListAsync(cancellationToken);
        if (matches.Count <= 1) return matches.FirstOrDefault();

        var exact = (email ?? "").Trim();
        return matches.SingleOrDefault(u => string.Equals(u.Email, exact, StringComparison.Ordinal));
    }

    /// <summary>Whether any user has this address in any letter case.</summary>
    public static Task<bool> AnyWithEmailAsync(
        this IQueryable<User> users, string? email, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(email);
        return users.AnyAsync(u => u.Email.ToLower() == normalized, cancellationToken);
    }
}
