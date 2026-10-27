using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DotNetSigningServer.Options;
using iText.Commons.Bouncycastle.Cert;
using Org.BouncyCastle.Crypto;

namespace DotNetSigningServer.Services;

/// <summary>
/// Reads the seal PFX once per <see cref="SealOptions"/> instance, keeps only the parsed
/// chain and key, and zeroes the file bytes straight after parsing. Before, every seal
/// (and every capability probe) decoded the PFX again and left a copy on the heap.
///
/// Keyed on the options instance rather than held by <see cref="PdfSealingService"/>,
/// which is scoped. A failed load is not cached, so a fixed file or password is
/// picked up without a restart.
/// </summary>
public static class SealCredentialStore
{
    private sealed record Loaded(IX509Certificate[] Chain, ICipherParameters PrivateKey);

    private static readonly ConditionalWeakTable<SealOptions, Loaded> Cache = new();
    private static readonly object Gate = new();

    public static (IX509Certificate[] Chain, ICipherParameters PrivateKey) Get(SealOptions options)
    {
        if (Cache.TryGetValue(options, out var hit)) return (hit.Chain, hit.PrivateKey);

        lock (Gate)
        {
            if (Cache.TryGetValue(options, out hit)) return (hit.Chain, hit.PrivateKey);

            byte[] pfxBytes;
            if (!string.IsNullOrWhiteSpace(options.PfxBase64))
            {
                pfxBytes = Convert.FromBase64String(options.PfxBase64);
            }
            else if (!string.IsNullOrWhiteSpace(options.PfxPath) && File.Exists(options.PfxPath))
            {
                pfxBytes = File.ReadAllBytes(options.PfxPath);
            }
            else
            {
                throw new InvalidOperationException("Seal certificate is not configured.");
            }

            try
            {
                var (chain, key) = PdfCryptoHelper.LoadFromPfxBytes(pfxBytes, options.PfxPassword);
                Cache.Add(options, new Loaded(chain, key));
                return (chain, key);
            }
            finally
            {
                // The parsed key is what signing uses; the container is not needed again.
                CryptographicOperations.ZeroMemory(pfxBytes);
            }
        }
    }
}
