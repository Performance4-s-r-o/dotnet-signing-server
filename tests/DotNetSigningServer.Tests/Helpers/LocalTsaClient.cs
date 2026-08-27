using DotNetSigningServer.Tests.Helpers;
using iText.Signatures;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;

namespace DotNetSigningServer.Tests.Helpers;

/// <summary>
/// An RFC 3161 authority that answers in-process.
///
/// Only the transport is replaced — the request encoding, the response parsing
/// and the token embedding all stay iText's, so a test using this exercises the
/// same timestamping path production does, without the suite depending on a
/// network TSA.
/// </summary>
internal sealed class LocalTsaClient : TSAClientBouncyCastle
{
    /// <summary>Arbitrary policy OID: nothing verifies it in these tests.</summary>
    private const string PolicyOid = "1.3.6.1.4.1.13762.3";

    private readonly Org.BouncyCastle.X509.X509Certificate _certificate;
    private readonly AsymmetricKeyParameter _privateKey;
    private int _serial;

    internal LocalTsaClient()
        : base("http://tsa.invalid/")
    {
        (_certificate, _privateKey) = TestHelpers.CreateTsaCertificate();
    }

    protected override byte[] GetTSAResponse(byte[] requestBytes)
    {
        var request = new TimeStampRequest(requestBytes);

        var tokenGenerator = new TimeStampTokenGenerator(
            _privateKey,
            _certificate,
            TspAlgorithms.Sha256,
            PolicyOid);
        tokenGenerator.SetCertificates(
            CollectionUtilities.CreateStore(new[] { _certificate }));

        var responseGenerator = new TimeStampResponseGenerator(tokenGenerator, TspAlgorithms.Allowed);
        var response = responseGenerator.Generate(
            request,
            BigInteger.ValueOf(Interlocked.Increment(ref _serial)),
            DateTime.UtcNow);

        return response.GetEncoded();
    }
}
