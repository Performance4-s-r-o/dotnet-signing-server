using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Filespec;
using iText.Signatures;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Pkcs;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #184: flattening and PDF/A conversion inside presign. Fixtures are made with iText; the PDF/A
/// side is checked with <see cref="PdfAConformanceCheck"/> (declaration + embedded fonts), the
/// rest by iText while it writes. veraPDF is not part of the suite.
/// </summary>
public class PresignPdfaTests : IDisposable
{
    private readonly List<string> _files = new();
    private static readonly LimitsOptions Limits = new() { RejectActiveContent = true };
    private static readonly PdfSafetyGuard Guard = new(TestHelpers.WrapOptions(Limits));
    private static readonly PdfConversionService Conversion = new(NullLogger<PdfConversionService>.Instance);

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    internal static byte[] Pdf(Action<PdfDocument> configure)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            doc.AddNewPage(PageSize.A4);
            configure(doc);
        }
        return ms.ToArray();
    }

    private static void Text(PdfDocument doc, PdfFont font, string text)
    {
        var canvas = new PdfCanvas(doc.GetFirstPage());
        canvas.BeginText().SetFontAndSize(font, 12).MoveText(50, 750).ShowText(text).EndText();
    }

    /// <summary>A filled form with a document script and embedded fonts: convertible once flattened.</summary>
    internal static byte[] ScriptedFormEmbedded() => Pdf(doc =>
    {
        Text(doc, AppFonts.Load(AppFontFamily.Sans), "Smlouva č. 1");
        var form = PdfAcroForm.GetAcroForm(doc, true);
        var field = new TextFormFieldBuilder(doc, "name").SetWidgetRectangle(new Rectangle(50, 600, 300, 24)).SetPage(1)
            .SetFont(AppFonts.Load(AppFontFamily.Sans)).CreateText();
        field.SetValue("Jana Nováková");
        form.AddField(field);
        doc.GetCatalog().GetNameTree(PdfName.JavaScript).AddEntry("init", PdfAction.CreateJavaScript("app.alert('hi')").GetPdfObject());
    });

    /// <summary>Text in plain Helvetica, which is never embedded: no PDF/A can come of it.</summary>
    internal static byte[] HelveticaText() => Pdf(doc =>
        Text(doc, PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA), "Hello"));

    private static byte[] EmbeddedText() => Pdf(doc => Text(doc, AppFonts.Load(AppFontFamily.Sans), "Hello"));

    private static byte[] WithAttachment() => Pdf(doc =>
    {
        Text(doc, AppFonts.Load(AppFontFamily.Sans), "Hello");
        doc.AddFileAttachment("a.txt", PdfFileSpec.CreateEmbeddedFileSpec(doc, new byte[] { 1, 2, 3 }, "a", "a.txt", null, null, null));
    });

    private static byte[] Converted(byte[] pdf, string conformance = "PDF/A-2B") =>
        Convert.FromBase64String(Conversion.ConvertToPdfA(new ConvertToPdfAInput { Conformance = conformance }, pdf));

    internal static byte[] SignedWithPfx(byte[] pdf)
    {
        var (_, pfx, password) = TestHelpers.CreateTestCertificate();
        var signed = new PdfSigningService().SignWithPfx(new PfxSignInput
        {
            PfxContent = pfx,
            PfxPassword = password,
            SignRect = new SignRect { X = 300, Y = 50, Width = 200, Height = 50 },
            Appearance = new SignatureAppearanceOptions { FontFamily = "Helvetica" },
        }, pdf);
        return Convert.FromBase64String(signed);
    }

    private static PresignPreparation.Result Prepare(byte[] pdf, bool flatten, bool convert, string? conformance = null) =>
        PresignPreparation.Prepare(pdf, new PreSignInput { FlattenForm = flatten, ConvertToPdfa = convert, PdfaConformance = conformance },
            Limits, Guard, Conversion);

    private static string Code(Action action) => Assert.Throws<ApiValidationException>(action).Code;

    private static PdfAConformance? Declared(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return doc.GetConformance().GetAConformance();
    }

    // ── preparation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flatten_then_convert_gives_a_declared_PDFA_without_active_content()
    {
        var result = Prepare(ScriptedFormEmbedded(), flatten: true, convert: true);

        Assert.Equal(PresignPreparation.FlattenOutcome.Flattened, result.Report.Flatten);
        Assert.Equal(1, result.Report.FlattenedFields);
        Assert.Contains(PdfFormFlattener.RemovedKinds.DocumentJavaScript, result.Report.Removed);
        Assert.Equal(PresignPreparation.PdfaOutcome.Converted, result.Report.Pdfa);
        Assert.Equal("PDF/A-2B", result.Report.Conformance);
        Assert.Equal(PdfAConformance.PDF_A_2B, result.KeepPdfA);
        Assert.Equal(PdfAConformance.PDF_A_2B, Declared(result.Pdf));
        Assert.Empty(PdfAConformanceCheck.Problems(result.Pdf, PdfAConformance.PDF_A_2B));
        Guard.EnsureSafePdf(result.Pdf, "test"); // full check, active content included
        Assert.Equal(1, result.Pages);
    }

    [Fact]
    public void Conversion_alone_does_not_strip_active_content()
    {
        // The endpoint refuses it earlier (400 PDF_ACTIVE_CONTENT); the conversion would not pass it either.
        Assert.Equal(PresignPreparation.ConversionFailedCode, Code(() => Prepare(ScriptedFormEmbedded(), flatten: false, convert: true)));
    }

    [Theory]
    [InlineData("pdf/a-3b", "PDF/A-3B")]
    [InlineData("PDFA_3B", "PDF/A-3B")]
    [InlineData(null, "PDF/A-2B")]
    [InlineData("", "PDF/A-2B")]
    public void Conformance_aliases_resolve(string? requested, string expected)
    {
        Assert.Equal(expected, PresignPreparation.ResolveConformance(requested).Name);
    }

    [Theory]
    [InlineData("PDF/A-1B")]
    [InlineData("PDF/A-2A")]
    [InlineData("PDF/A-2U")]
    [InlineData("PDF/A-4")]
    [InlineData("nonsense")]
    public void Other_conformances_are_refused_not_mapped_to_a_default(string requested)
    {
        Assert.Equal(PresignPreparation.ConformanceUnsupportedCode, Code(() => PresignPreparation.ResolveConformance(requested)));
    }

    [Fact]
    public void A_font_that_is_not_embedded_is_refused_not_passed_off_as_PDFA()
    {
        Assert.Equal(PresignPreparation.ConversionFailedCode, Code(() => Prepare(HelveticaText(), flatten: false, convert: true)));
    }

    [Fact]
    public void Embedded_files_are_refused_rather_than_dropped()
    {
        Assert.Equal(PresignPreparation.ConversionFailedCode, Code(() => Prepare(WithAttachment(), flatten: false, convert: true)));
    }

    [Fact]
    public void A_signed_document_that_is_not_PDFA_is_refused()
    {
        Assert.Equal(PdfFormFlattener.AlreadySignedCode, Code(() => Prepare(SignedWithPfx(EmbeddedText()), flatten: true, convert: true)));
    }

    [Fact]
    public void A_signed_PDFA_is_signed_on_without_conversion_or_flattening()
    {
        var signed = SignedWithPfx(Converted(EmbeddedText(), "PDF/A-3B"));

        var result = Prepare(signed, flatten: true, convert: true);

        Assert.Same(signed, result.Pdf);
        Assert.Equal(PresignPreparation.FlattenOutcome.SkippedSigned, result.Report.Flatten);
        Assert.Equal(PresignPreparation.PdfaOutcome.AlreadyPdfa, result.Report.Pdfa);
        Assert.Equal("PDF/A-3B", result.Report.Conformance);
        Assert.Equal(PdfAConformance.PDF_A_3B, result.KeepPdfA);
    }

    [Fact]
    public void Flattening_alone_skips_a_signed_document_but_still_guards_it()
    {
        var signed = SignedWithPfx(EmbeddedText());
        var result = Prepare(signed, flatten: true, convert: false);
        Assert.Same(signed, result.Pdf);
        Assert.Equal(PresignPreparation.FlattenOutcome.SkippedSigned, result.Report.Flatten);
        Assert.Null(result.Report.Pdfa);
        Assert.Null(result.KeepPdfA);
    }

    [Fact]
    public void The_check_finds_a_font_that_is_not_embedded_in_an_annotation_appearance()
    {
        var pdf = Converted(EmbeddedText());
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(ms), new StampingProperties().UseAppendMode()))
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, "late").SetWidgetRectangle(new Rectangle(50, 500, 200, 20)).SetPage(1)
                .SetFont(PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA)).CreateText();
            field.SetValue("x");
            form.AddField(field);
        }
        Assert.Contains(PdfAConformanceCheck.FontNotEmbedded, PdfAConformanceCheck.Problems(ms.ToArray(), PdfAConformance.PDF_A_2B));
    }

    [Fact]
    public void The_check_reports_a_missing_declaration()
    {
        Assert.Contains(PdfAConformanceCheck.NotDeclared, PdfAConformanceCheck.Problems(EmbeddedText(), PdfAConformance.PDF_A_2B));
        Assert.Contains(PdfAConformanceCheck.NotDeclared, PdfAConformanceCheck.Problems(Converted(EmbeddedText()), PdfAConformance.PDF_A_3B));
    }

    // ── presign and sign keep PDF/A ─────────────────────────────────────────────────────────────

    private (string Path, string Hash, string CertPem, string Pfx, string Password) Presign(
        byte[] pdf, PdfAConformance? keep, Action<PreSignInput>? configure = null)
    {
        var (certPem, pfx, password) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            Reason = "Souhlas",
            Location = "Praha",
            SignRect = new SignRect { X = 50, Y = 100, Width = 200, Height = 60 },
        };
        configure?.Invoke(input);
        var (path, hash) = new PdfSigningService().HandlePreSign(input, pdf, "Signature1", keep);
        _files.Add(path);
        return (path, hash, certPem, pfx, password);
    }

    private static string SignHash(string hashHex, string pfx, string password)
    {
        using var pfxMs = new MemoryStream(Convert.FromBase64String(pfx));
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(pfxMs, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);
        var signer = Org.BouncyCastle.Security.SignerUtilities.GetSigner("SHA256withRSA");
        signer.Init(true, store.GetKey(alias).Key);
        var hash = Convert.FromHexString(hashHex);
        signer.BlockUpdate(hash, 0, hash.Length);
        return Convert.ToHexString(signer.GenerateSignature());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("qr")]
    [InlineData("link")]
    public void Presign_and_sign_keep_the_document_PDFA_and_the_signature_valid(string verificationMode)
    {
        var prepared = Prepare(ScriptedFormEmbedded(), flatten: true, convert: true);
        // No font family: the default must be an embedded font in a PDF/A document.
        var (path, hash, certPem, pfx, password) = Presign(prepared.Pdf, prepared.KeepPdfA, i =>
        {
            i.VerificationUrl = "https://example.org/verify/abc";
            i.VerificationMode = verificationMode;
            i.Fields = new List<PreSignFieldInput>
            {
                new() { Definition = new PdfFieldDefinition { FieldName = "note", Rect = new SignRect { X = 50, Y = 400, Width = 200, Height = 20 } }, Value = "Žluťoučký kůň" },
            };
        });

        var signed = Convert.FromBase64String(new PdfSigningService().HandleSign(
            new SignInput { Id = "x", SignedHash = SignHash(hash, pfx, password) }, path, certPem, "Signature1"));

        Assert.Equal(PdfAConformance.PDF_A_2B, Declared(signed));
        Assert.Empty(PdfAConformanceCheck.Problems(signed, PdfAConformance.PDF_A_2B));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        Assert.True(util.SignatureCoversWholeDocument("Signature1"));
        Assert.True(util.ReadSignatureData("Signature1").VerifySignatureIntegrityAndAuthenticity());
    }

    [Fact]
    public void A_stamped_field_in_a_font_that_is_not_embedded_is_refused()
    {
        var prepared = Prepare(EmbeddedText(), flatten: false, convert: true);

        var ex = Assert.Throws<ApiValidationException>(() => Presign(prepared.Pdf, prepared.KeepPdfA, i =>
            i.Fields = new List<PreSignFieldInput>
            {
                new()
                {
                    Definition = new PdfFieldDefinition
                    {
                        FieldName = "mark", FontName = PdfFontName.ZapfDingbats,
                        Rect = new SignRect { X = 50, Y = 400, Width = 40, Height = 20 },
                    },
                    Value = "4",
                },
            }));
        Assert.Equal(PresignPreparation.NotPreservedCode, ex.Code);
    }

    [Fact]
    public void Without_keep_the_same_stamp_presigns_as_before()
    {
        var prepared = Prepare(EmbeddedText(), flatten: false, convert: true);
        var (path, _, _, _, _) = Presign(prepared.Pdf, keep: null, i =>
            i.Fields = new List<PreSignFieldInput>
            {
                new()
                {
                    Definition = new PdfFieldDefinition
                    {
                        FieldName = "mark", FontName = PdfFontName.ZapfDingbats,
                        Rect = new SignRect { X = 50, Y = 400, Width = 40, Height = 20 },
                    },
                    Value = "4",
                },
            });
        Assert.True(File.Exists(path));
    }
}
