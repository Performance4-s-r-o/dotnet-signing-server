using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services;

/// <summary>
/// E-mail addresses never go to the log in clear text (#217): logs cannot be erased selectively.
/// <see cref="Email"/> gives a short HMAC tag — the same address gives the same tag, so one
/// failure can be followed through the log, but the address cannot be read back or guessed
/// without the key. The key is random per process (no new setting; tags do not match across
/// restarts, which is the safer side of that trade).
/// </summary>
public static partial class LogRedaction
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [GeneratedRegex(@"[\p{L}\p{N}._%+'-]+(?:@|%40)[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    public static string Email(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "email:none";
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant()));
        return "email:" + Convert.ToHexString(mac, 0, 4).ToLowerInvariant();
    }

    /// <summary>Free text (a provider's error body) with every address replaced by its tag.</summary>
    public static string Text(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : EmailPattern().Replace(text, m => Email(m.Value.Replace("%40", "@", StringComparison.OrdinalIgnoreCase)));
}
