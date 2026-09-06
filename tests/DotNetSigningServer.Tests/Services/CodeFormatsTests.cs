using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using ZXing;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The one list of symbologies, the two directions that derive from it, and the
/// guards that let the unchecked linear formats be looked for at all.
///
/// There used to be three lists and they disagreed. The engine could write nine
/// formats but only looked for four when reading, so a Code 128 it had stamped
/// itself came back as "no codes found". These tests exist so that cannot come
/// back: the JSON enum, the write switch and the scanner all have to agree with
/// the catalogue, and a format added to one without the others fails here.
/// </summary>
public class CodeFormatsTests
{
    private static IList<BarcodeFormat> Formats(string codeType) =>
        PdfUtilityApiController.ParseFormats(codeType).Formats;

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
    public void Code93CountsAsSelfChecking()
    {
        // Code 93 carries two MANDATORY check characters (C and K) and the decoder
        // verifies both on every read. It was filed with the unchecked formats,
        // which confused it with Code 39 — there the check digit is optional.
        Assert.True(CodeFormats.Find("code93")!.SelfChecking);
        Assert.False(CodeFormats.Find("code39")!.SelfChecking);
    }

    [Fact]
    public void OnlyThreeFormatsNeedAGuard()
    {
        var unchecked_ = CodeFormats.All.Where(f => !f.SelfChecking).Select(f => f.Id).OrderBy(x => x);
        Assert.Equal(new[] { "codabar", "code39", "itf" }, unchecked_);

        // ...and each of them actually carries one. An entry that is neither
        // self-checking nor guarded would be reported straight off a table rule.
        foreach (var format in CodeFormats.All.Where(f => !f.SelfChecking))
        {
            Assert.True(
                format.RequireCheckDigitWhenUnnamed || format.MinLengthWhenUnnamed > 0,
                $"{format.Id} has no check digit and no guard");
        }
    }

    // ── What a scan looks for ────────────────────────────────────────────────

    [Fact]
    public void AnUnnamedScanLooksForEverything_ButGuarded()
    {
        var plan = PdfUtilityApiController.ParseFormats("any");

        // The formats with no check digit used to be left out entirely, so a page
        // really carrying a Code 39 came back empty. They are in now.
        Assert.Contains(BarcodeFormat.CODE_39, plan.Formats);
        Assert.Contains(BarcodeFormat.ITF, plan.Formats);
        Assert.Contains(BarcodeFormat.CODABAR, plan.Formats);
        Assert.Contains(BarcodeFormat.CODE_128, plan.Formats);
        Assert.Contains(BarcodeFormat.QR_CODE, plan.Formats);

        Assert.True(plan.Guarded);
    }

    [Fact]
    public void AnUnnamedScanDemandsTheCode39CheckDigit()
    {
        // The strongest guard available, and the only one that rejects the READ
        // rather than the result: a stray bar pattern has to satisfy modulo 43 too.
        var plan = PdfUtilityApiController.ParseFormats("any");
        Assert.True(CodeFormats.RequiresCode39CheckDigit(plan.Specs, plan.Guarded));
    }

    [Fact]
    public void NamingCode39LiftsTheCheckDigitRequirement()
    {
        // Most real Code 39 is printed without a check digit — the standard makes it
        // optional. Asking for the format by name is the caller saying the page holds
        // one, so demanding a digit that was never printed would find nothing.
        var plan = PdfUtilityApiController.ParseFormats("code39");

        Assert.False(plan.Guarded);
        Assert.False(CodeFormats.RequiresCode39CheckDigit(plan.Specs, plan.Guarded));
        Assert.Equal(new[] { BarcodeFormat.CODE_39 }, plan.Formats);
    }

    [Fact]
    public void AskingForAGroupIsStillGuarded()
    {
        // "Look for barcodes" is not the same claim as "this page holds a Code 39
        // without a check digit". Only naming one format is specific enough.
        var plan = PdfUtilityApiController.ParseFormats("1d");

        Assert.True(plan.Guarded);
        Assert.Contains(BarcodeFormat.CODE_39, plan.Formats);
        Assert.DoesNotContain(BarcodeFormat.QR_CODE, plan.Formats);
    }

    [Fact]
    public void AskingFor2dExcludesTheLinearOnes()
    {
        var formats = Formats("2d");

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
        var formats = Formats(codeType);

        Assert.Single(formats);
        Assert.Equal(expected, formats[0]);
    }

    [Fact]
    public void AnUnknownNameFallsBackToTheGuardedScan()
    {
        // Not an error: the name may come from an old template. Falling back finds
        // the codes that are actually there instead of nothing — but guarded, since
        // an unrecognised word is not a claim about what is on the page.
        var plan = PdfUtilityApiController.ParseFormats("nonsense-format");

        Assert.Contains(BarcodeFormat.QR_CODE, plan.Formats);
        Assert.True(plan.Guarded);
    }

    // ── The length floor ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("itf", "1234567", false)]        // seven digits — noise length
    [InlineData("itf", "12345678", true)]        // eight — the floor
    [InlineData("itf", "12345678901231", true)]  // ITF-14, the dominant real use
    public void ShortItfIsNotReportedWhenNobodyAskedForIt(string id, string text, bool kept)
    {
        var spec = CodeFormats.Find(id)!;
        Assert.Equal(kept, CodeFormats.IsPlausibleWhenUnnamed(spec, text));
    }

    [Fact]
    public void CodabarStartAndStopDoNotCountTowardsTheFloor()
    {
        var spec = CodeFormats.Find("codabar")!;

        // The decoder is configured to return the A...A delimiters. Counting them
        // would let a six-digit noise read pass as eight characters.
        Assert.False(CodeFormats.IsPlausibleWhenUnnamed(spec, "A1234567A"));
        Assert.True(CodeFormats.IsPlausibleWhenUnnamed(spec, "A12345678A"));
    }

    [Fact]
    public void FormatsWithTheirOwnCheckDigitHaveNoLengthFloor()
    {
        // An EAN-8 is eight digits and a UPC-E can be six. Applying a floor meant
        // for noise-prone symbologies would throw away real, verified reads.
        foreach (var format in CodeFormats.All.Where(f => f.SelfChecking))
        {
            Assert.True(CodeFormats.IsPlausibleWhenUnnamed(format, "12345"), format.Id);
        }
    }
}
