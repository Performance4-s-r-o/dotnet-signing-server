using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using ZXing;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The one list of symbologies, and the two directions that derive from it.
///
/// There used to be three lists and they disagreed. The engine could write nine
/// formats but only looked for four when reading, so a Code 128 it had stamped
/// itself came back as "no codes found". These tests exist so that cannot come
/// back: the JSON enum, the write switch and the scanner all have to agree with
/// the catalogue, and a format added to one without the others fails here.
/// </summary>
public class CodeFormatsTests
{
    [Fact]
    public void EveryWireNameResolvesToAFormat()
    {
        // PdfBarcodeFormat is the public JSON contract. A value there with no entry
        // in the catalogue is a symbology callers may ask for and nothing can
        // render — it would fall through the switch to Code 128 and silently stamp
        // the wrong barcode.
        var wireNames = new[]
        {
            "code128", "code-128", "qr", "qrcode", "qr-code",
            "datamatrix", "data-matrix", "dm", "pdf417",
            "ean13", "ean-13", "ean8", "ean-8",
            "upc", "upca", "upc-a", "upce", "upc-e",
            "code39", "code-39", "itf", "interleaved2of5", "i2of5",
            "codabar", "coda-bar",
        };

        foreach (var name in wireNames)
        {
            Assert.True(CodeFormats.Find(name) != null, $"no catalogue entry for '{name}'");
        }
    }

    [Fact]
    public void EveryFormatIsReadableOrWritable_AndSaysWhich()
    {
        Assert.All(CodeFormats.All, f => Assert.True(f.CanRead || f.CanWrite, $"{f.Id} does neither"));
        // Readable means there is a decoder to hand it to.
        Assert.All(CodeFormats.Readable, f => Assert.NotNull(f.ReadFormat));
    }

    [Fact]
    public void TheEngineCanReadEverythingItCanWrite()
    {
        // This is the failure that started it: a Code 128 the server had stamped
        // itself was invisible to its own scanner.
        var writeOnly = CodeFormats.Writable.Where(f => !f.CanRead).Select(f => f.Id).ToList();
        Assert.Empty(writeOnly);
    }

    [Fact]
    public void OnlyTheFormatsWithNoGeneratorAreReadOnly()
    {
        // Read-only is allowed, but only where iText genuinely ships no generator.
        // Anything else in this list means a writer was forgotten.
        var readOnly = CodeFormats.Readable.Where(f => !f.CanWrite).Select(f => f.Id).OrderBy(x => x);
        Assert.Equal(new[] { "aztec", "code93" }, readOnly);
    }

    [Fact]
    public void AnUnnamedScanLooksForSelfCheckingFormatsOnly()
    {
        var formats = PdfUtilityApiController.ParseFormats("any");

        // The reported gap: Code 128 was unreadable.
        Assert.Contains(BarcodeFormat.CODE_128, formats);
        Assert.Contains(BarcodeFormat.QR_CODE, formats);
        Assert.Contains(BarcodeFormat.UPC_E, formats);

        // No mandatory check digit: on a 300 DPI render of a table these report
        // values that are not on the page.
        Assert.DoesNotContain(BarcodeFormat.CODE_39, formats);
        Assert.DoesNotContain(BarcodeFormat.ITF, formats);
        Assert.DoesNotContain(BarcodeFormat.CODABAR, formats);
        Assert.DoesNotContain(BarcodeFormat.CODE_93, formats);
    }

    [Fact]
    public void AskingForLinearCodesIncludesTheUncheckedOnes()
    {
        var formats = PdfUtilityApiController.ParseFormats("1d");

        Assert.Contains(BarcodeFormat.CODE_39, formats);
        Assert.Contains(BarcodeFormat.ITF, formats);
        Assert.Contains(BarcodeFormat.CODABAR, formats);
        Assert.Contains(BarcodeFormat.CODE_128, formats);
        // Asking for linear codes must not quietly scan for QR as well.
        Assert.DoesNotContain(BarcodeFormat.QR_CODE, formats);
    }

    [Fact]
    public void AskingFor2dExcludesTheLinearOnes()
    {
        var formats = PdfUtilityApiController.ParseFormats("2d");

        Assert.Contains(BarcodeFormat.QR_CODE, formats);
        Assert.Contains(BarcodeFormat.AZTEC, formats);
        Assert.DoesNotContain(BarcodeFormat.CODE_128, formats);
    }

    [Theory]
    [InlineData("code128", BarcodeFormat.CODE_128)]
    [InlineData("code-128", BarcodeFormat.CODE_128)]
    [InlineData("ean13", BarcodeFormat.EAN_13)]
    [InlineData("upc-e", BarcodeFormat.UPC_E)]
    [InlineData("codabar", BarcodeFormat.CODABAR)]
    [InlineData("qr", BarcodeFormat.QR_CODE)]
    public void NamingAFormatScansForThatOneOnly(string codeType, BarcodeFormat expected)
    {
        var formats = PdfUtilityApiController.ParseFormats(codeType);

        Assert.Single(formats);
        Assert.Equal(expected, formats[0]);
    }

    [Fact]
    public void AnUnknownNameFallsBackToTheDefaultScan()
    {
        // Not an error: the name may come from an old template. Falling back to the
        // safe set finds the codes that are actually there instead of nothing.
        var formats = PdfUtilityApiController.ParseFormats("nonsense-format");

        Assert.Contains(BarcodeFormat.QR_CODE, formats);
        Assert.DoesNotContain(BarcodeFormat.CODE_39, formats);
    }
}
