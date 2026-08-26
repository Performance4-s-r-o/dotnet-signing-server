using iText.Commons.Bouncycastle.Cert;
using iText.Signatures;

namespace DotNetSigningServer.Services;

/// <summary>
/// Long-term validation means fetching revocation data from URLs printed inside
/// the certificate the caller handed us — an outbound request the caller chooses
/// the destination of, exactly the primitive the TSA URL already had to be
/// guarded against.
///
/// The rule differs from the TSA one in one respect: OCSP and CRL are normally
/// served over plain HTTP and that is fine, because the responses are signed and
/// verified on their own. Demanding HTTPS would fail against most real
/// certificates. What is not fine is a URL aimed at the network the server sits
/// on, so the host is checked before anything is fetched.
/// </summary>
internal static class RevocationEndpointGuard
{
    /// <summary>
    /// True when the URL is safe to fetch: absolute, http(s), and not resolving to
    /// a loopback, private, link-local or otherwise internal address.
    /// </summary>
    internal static bool IsFetchable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Fails closed: a host that cannot be resolved is treated as unsafe.
        return !PdfCryptoHelper.ResolvesToPrivateAddress(uri.DnsSafeHost);
    }
}

/// <summary>
/// OCSP client that refuses to call an endpoint pointing into private address
/// space. Delegates to iText once the URL passes.
/// </summary>
internal sealed class GuardedOcspClient : IOcspClient
{
    private readonly IOcspClient _inner;

    internal GuardedOcspClient(IOcspClient? inner = null)
    {
        _inner = inner ?? new OcspClientBouncyCastle();
    }

    public byte[]? GetEncoded(IX509Certificate checkCert, IX509Certificate issuerCert, string url)
    {
        // A null url makes the inner client derive one from the certificate, which
        // would slip past the guard — resolve it here so there is always something
        // to check, and hand the checked value on explicitly.
        var target = string.IsNullOrWhiteSpace(url) ? SafeOcspUrl(checkCert) : url;
        if (!RevocationEndpointGuard.IsFetchable(target))
        {
            return null;
        }

        return _inner.GetEncoded(checkCert, issuerCert, target);
    }

    private static string? SafeOcspUrl(IX509Certificate certificate)
    {
        try
        {
            return CertificateUtil.GetOCSPURL(certificate);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// CRL client with the same guard. Distribution points that fail the check are
/// skipped rather than fetched; the caller ends up with whatever the safe points
/// returned, which LtvVerification then treats as the available evidence.
/// </summary>
internal sealed class GuardedCrlClient : ICrlClient
{
    private readonly ICrlClient _inner;

    internal GuardedCrlClient(ICrlClient? inner = null)
    {
        _inner = inner ?? new CrlClientOnline();
    }

    public ICollection<byte[]> GetEncoded(IX509Certificate checkCert, string url)
    {
        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(url))
        {
            targets.Add(url);
        }
        else
        {
            try
            {
                targets.AddRange(CertificateUtil.GetCRLURLs(checkCert) ?? new List<string>());
            }
            catch
            {
                // No usable distribution point — nothing to fetch.
            }
        }

        var collected = new List<byte[]>();
        foreach (var target in targets.Where(RevocationEndpointGuard.IsFetchable))
        {
            try
            {
                var encoded = _inner.GetEncoded(checkCert, target);
                if (encoded != null) collected.AddRange(encoded);
            }
            catch
            {
                // One unreachable distribution point must not abandon the others.
            }
        }

        return collected;
    }
}
