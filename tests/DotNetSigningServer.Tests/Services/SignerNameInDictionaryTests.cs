using System;
using System.IO;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Signatures;
using Xunit;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The /Name entry of the signature dictionary.
///
/// A reader's "Signed by" reads this first and falls back to the certificate
/// subject. On every online signature that certificate is our platform seal or
/// the customer's vault key, so without /Name the appearance said "Jane Doe"
/// and clicking it said the name of a company the signer had never heard of.
/// </summary>
public class SignerNameInDictionaryTests
{
    [Fact]
    public void SignWithPfx_WritesTheSignerIntoTheSignatureDictionary()
    {
        var signed = SignWith("Jane Doe");
        Assert.Equal("Jane Doe", ReadSignatureName(signed));
    }

    [Fact]
    public void SignWithPfx_CarriesAnOrganisationJustAsWell()
    {
        // Pečeť jménem firmy posílá jako "podepisujícího" organizaci — engine
        // o tom rozdílu nic neví a nemá vědět, rozhoduje volající.
        var signed = SignWith("Performance4 s.r.o.");
        Assert.Equal("Performance4 s.r.o.", ReadSignatureName(signed));
    }

    [Fact]
    public void SignWithPfx_LeavesNameUnsetWhenNoSignerIsGiven()
    {
        // Prázdné /Name by tvrdilo, že dokument podepsal nikdo. Chybějící klíč
        // nechá čtečku spadnout zpátky na certifikát, což je aspoň pravda.
        var name = ReadSignatureName(SignWith(null));
        Assert.True(string.IsNullOrEmpty(name), $"expected no /Name, got '{name}'");
    }

    private static byte[] SignWith(string? signerName)
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var service = new PdfSigningService(
            TestHelpers.WrapOptions(new SealOptions()),
            TestHelpers.WrapOptions(new EvidenceOptions()),
            new PdfVisualSigningService());

        var result = service.SignWithPfx(new PfxSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            PfxContent = pfxBase64,
            PfxPassword = password,
            Location = "Test",
            Reason = "Unit Test",
            SignerName = signerName,
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 }
        });

        return Convert.FromBase64String(result);
    }

    private static string? ReadSignatureName(byte[] pdfBytes)
    {
        using var ms = new MemoryStream(pdfBytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        var util = new SignatureUtil(doc);
        var names = util.GetSignatureNames();
        Assert.NotEmpty(names);
        return util.GetSignature(names[0]).GetName();
    }
}
