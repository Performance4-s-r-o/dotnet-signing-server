using iText.Commons.Digest;
using iText.Signatures;

namespace DotNetSigningServer.Services;

/// <summary>
/// An authority the caller named: URL plus optional HTTP Basic credentials.
/// </summary>
public sealed record TsaEndpoint(string Url, string? Username, string? Password)
{
    /// <summary>Null when no URL was given — the caller asked for no backup.</summary>
    public static TsaEndpoint? From(string? url, string? username, string? password) =>
        string.IsNullOrWhiteSpace(url) ? null : new TsaEndpoint(url, username, password);
}

/// <summary>
/// Asks the primary authority for a timestamp and, when that fails, the backup.
///
/// The portal names a backup for its platform authorities so that one TSA's
/// outage does not refuse every timestamped signature until someone switches
/// providers by hand. The digest does not depend on which authority answers,
/// so either token completes the same signature.
///
/// Any failure of the primary counts — network, timeout, an HTTP error or a
/// response the TSP layer rejects — because from the caller's side they all mean
/// "no timestamp from this one". When the backup fails too, its exception is the
/// one that surfaces, so the error mapping upstream sees a TSA failure as before.
/// </summary>
public sealed class FallbackTsaClient : ITSAClient
{
    private readonly ITSAClient _primary;
    private readonly ITSAClient _backup;
    private readonly string _primaryUrl;
    private readonly string _backupUrl;

    public FallbackTsaClient(ITSAClient primary, ITSAClient backup, string primaryUrl, string backupUrl)
    {
        _primary = primary;
        _backup = backup;
        _primaryUrl = primaryUrl;
        _backupUrl = backupUrl;
    }

    /// <summary>
    /// Space reserved for the token in the signature container. The larger of the
    /// two, since which one answers is only known at stamping time.
    /// </summary>
    public int GetTokenSizeEstimate() =>
        Math.Max(_primary.GetTokenSizeEstimate(), _backup.GetTokenSizeEstimate());

    public IMessageDigest GetMessageDigest() => _primary.GetMessageDigest();

    public byte[] GetTimeStampToken(byte[] imprint)
    {
        try
        {
            return _primary.GetTimeStampToken(imprint);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Stdout reaches Loki; the signing service has no logger to hand.
            Console.Error.WriteLine(
                $"warn: TSA primary {_primaryUrl} failed ({ex.GetType().Name}: {ex.Message}); trying backup {_backupUrl}");
            return _backup.GetTimeStampToken(imprint);
        }
    }
}
