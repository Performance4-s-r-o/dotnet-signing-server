using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// Fixed-window counter for sign-in, 2FA, password reset and re-auth attempts (#80). In the
/// database rather than in the process, so every instance counts the same attempts and a
/// restart does not hand out a fresh window. See <c>AuthAttemptLimiter</c>.
/// </summary>
public class AuthAttemptCounter
{
    [Key]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTimeOffset WindowStart { get; set; }
}
