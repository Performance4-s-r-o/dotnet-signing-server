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
/// picked up without a restart. When the source is a file, its size and modification
/// time are part of the key: replacing the file (certificate rotation) reloads it.
/// </summary>
public static class SealCredentialStore
{
    private sealed record Loaded(IX509Certificate[] Chain, ICipherParameters PrivateKey, string? FileStamp);

    private static readonly ConditionalWeakTable<SealOptions, Loaded> Cache = new();
    private static readonly object Gate = new();

    /// <summary>Size and mtime of the PFX file (only when it is the source), so a rotated file is picked up without a restart.</summary>
    private static string? StampOf(SealOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PfxBase64) || string.IsNullOrWhiteSpace(options.PfxPath)) return null;
        try
        {
            var info = new FileInfo(options.PfxPath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>A file that has vanished keeps serving the key already loaded; a changed one reloads.</summary>
    private static bool Fresh(Loaded hit, SealOptions options)
    {
        var now = StampOf(options);
        return now == hit.FileStamp || (now == null && hit.FileStamp != null);
    }

    public static (IX509Certificate[] Chain, ICipherParameters PrivateKey) Get(SealOptions options)
    {
        if (Cache.TryGetValue(options, out var hit) && Fresh(hit, options)) return (hit.Chain, hit.PrivateKey);

        lock (Gate)
        {
            if (Cache.TryGetValue(options, out hit))
            {
                if (Fresh(hit, options)) return (hit.Chain, hit.PrivateKey);
                Cache.Remove(options);
            }

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
                Cache.Add(options, new Loaded(chain, key, StampOf(options)));
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
