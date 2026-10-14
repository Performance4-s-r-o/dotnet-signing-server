using DotNetSigningServer.Exceptions;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Math;

namespace DotNetSigningServer.Services;

/// <summary>
/// A PKCS#12 file states its own key-derivation cost: the iteration count of the
/// MAC and of each encrypted bag is a number inside the file. A few hundred bytes
/// that say "100 000 000" make the server derive keys for minutes. The count is
/// read from the structure and refused BEFORE the store is loaded.
/// </summary>
public static class PfxGuard
{
    public static int MaxBytes { get; set; } = 50 * 1024;
    public static long MaxIterations { get; set; } = 1_000_000;

    public static void EnsureSafe(byte[] pfx)
    {
        if (pfx.Length > MaxBytes)
        {
            throw new ApiValidationException("PFX_TOO_LARGE");
        }

        BigInteger highest;
        try
        {
            highest = HighestIterationCount(pfx);
        }
        catch (Exception ex) when (ex is not ApiValidationException)
        {
            // Not a structure this reader understands: let the real loader give the real error.
            return;
        }

        if (highest.CompareTo(BigInteger.ValueOf(MaxIterations)) > 0)
        {
            throw new ApiValidationException("PFX_TOO_EXPENSIVE");
        }
    }

    private static BigInteger HighestIterationCount(byte[] pfx)
    {
        var parsed = Pfx.GetInstance(Asn1Sequence.GetInstance(Asn1Object.FromByteArray(pfx)));
        var highest = parsed.MacData?.Iterations.Value ?? BigInteger.Zero;

        var content = parsed.AuthSafe;
        if (!PkcsObjectIdentifiers.Data.Equals(content.ContentType)) return highest;

        var safe = AuthenticatedSafe.GetInstance(Asn1OctetString.GetInstance(content.Content).GetOctets() is { } octets
            ? Asn1Object.FromByteArray(octets)
            : null);

        foreach (var info in safe.GetContentInfo())
        {
            if (PkcsObjectIdentifiers.EncryptedData.Equals(info.ContentType))
            {
                var enc = EncryptedData.GetInstance(info.Content);
                highest = Max(highest, Iterations(enc.EncryptionAlgorithm));
            }
            else if (PkcsObjectIdentifiers.Data.Equals(info.ContentType))
            {
                var bags = Asn1Sequence.GetInstance(Asn1Object.FromByteArray(Asn1OctetString.GetInstance(info.Content).GetOctets()));
                foreach (var element in bags)
                {
                    var bag = SafeBag.GetInstance(element);
                    if (PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag.Equals(bag.BagID))
                    {
                        var key = EncryptedPrivateKeyInfo.GetInstance(bag.BagValue);
                        highest = Max(highest, Iterations(key.EncryptionAlgorithm));
                    }
                }
            }
        }

        return highest;
    }

    private static BigInteger Max(BigInteger a, BigInteger b) => a.CompareTo(b) >= 0 ? a : b;

    private static BigInteger Iterations(AlgorithmIdentifier algorithm)
    {
        if (PkcsObjectIdentifiers.IdPbeS2.Equals(algorithm.Algorithm))
        {
            var pbes2 = PbeS2Parameters.GetInstance(algorithm.Parameters);
            var kdf = pbes2.KeyDerivationFunc;
            if (PkcsObjectIdentifiers.IdPbkdf2.Equals(kdf.Algorithm))
            {
                return Pbkdf2Params.GetInstance(kdf.Parameters).IterationCount;
            }
            // A derivation function this guard cannot price (scrypt, ...) is refused, not waved through.
            throw new ApiValidationException("PFX_TOO_EXPENSIVE");
        }

        // pbeWithSHAAnd3-KeyTripleDES-CBC and the other PKCS#12 PBE schemes.
        return Pkcs12PbeParams.GetInstance(algorithm.Parameters).Iterations;
    }
}
