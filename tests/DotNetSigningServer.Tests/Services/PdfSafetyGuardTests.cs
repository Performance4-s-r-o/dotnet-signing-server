using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Filespec;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// What the guard refuses and — just as important — what it must let through:
/// embedded files (our own evidence, PDF/A-3 invoices) and form-field scripts
/// (how interactive forms are built).
/// </summary>
public class PdfSafetyGuardTests
{
    private static PdfSafetyGuard CreateGuard(LimitsOptions? options = null)
        => new(TestHelpers.WrapOptions(options ?? new LimitsOptions()));

    private static byte[] BuildPdf(Action<PdfDocument> configure, WriterProperties? writerProperties = null)
    {
        using var ms = new MemoryStream();
        var writer = writerProperties == null ? new PdfWriter(ms) : new PdfWriter(ms, writerProperties);
        using (var pdf = new PdfDocument(writer))
        {
            pdf.AddNewPage(PageSize.A4);
            configure(pdf);
        }
        return ms.ToArray();
    }

    [Fact]
    public void PlainPdf_Passes()
    {
        var pdf = BuildPdf(_ => { });
        CreateGuard().EnsureSafePdf(pdf, "test");
        CreateGuard().EnsureSafePdf(Convert.ToBase64String(pdf), "test");
    }

    [Fact]
    public void NotBase64_IsInvalid()
    {
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf("not base64!!", "test"));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void NoHeader_IsInvalid()
    {
        var ex = Assert.Throws<ApiValidationException>(
            () => CreateGuard().EnsureSafePdf("<html>hello</html>"u8.ToArray(), "test"));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void HeaderTooFarIn_IsInvalid()
    {
        var pdf = BuildPdf(_ => { });
        var padded = new byte[1100 + pdf.Length];
        Array.Fill(padded, (byte)' ', 0, 1100);
        pdf.CopyTo(padded, 1100);
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(padded, "test"));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void Truncated_IsInvalid()
    {
        var pdf = BuildPdf(_ => { });
        var truncated = pdf.Take(pdf.Length / 2).ToArray();
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(truncated, "test"));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void Encrypted_IsRefused_EvenWithOwnerPasswordOnly()
    {
        var props = new WriterProperties().SetStandardEncryption(
            userPassword: null,
            ownerPassword: "owner"u8.ToArray(),
            permissions: EncryptionConstants.ALLOW_PRINTING,
            encryptionAlgorithm: EncryptionConstants.ENCRYPTION_AES_128);
        var pdf = BuildPdf(_ => { }, props);
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ENCRYPTED", ex.Code);
    }

    [Fact]
    public void UserPassword_IsRefused()
    {
        var props = new WriterProperties().SetStandardEncryption(
            userPassword: "user"u8.ToArray(),
            ownerPassword: "owner"u8.ToArray(),
            permissions: EncryptionConstants.ALLOW_PRINTING,
            encryptionAlgorithm: EncryptionConstants.ENCRYPTION_AES_128);
        var pdf = BuildPdf(_ => { }, props);
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ENCRYPTED", ex.Code);
    }

    [Fact]
    public void TooManyPages_IsRefused()
    {
        var pdf = BuildPdf(doc =>
        {
            for (var i = 0; i < 5; i++) doc.AddNewPage();
        });
        var ex = Assert.Throws<ApiValidationException>(
            () => CreateGuard(new LimitsOptions { PdfMaxPages = 3 }).EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_TOO_MANY_PAGES", ex.Code);
        CreateGuard(new LimitsOptions { PdfMaxPages = 6 }).EnsureSafePdf(pdf, "test");
    }

    [Fact]
    public void JavaScriptOnOpen_IsRefused()
    {
        var pdf = BuildPdf(doc => doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript("app.alert('hi')")));
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void JavaScriptNameTree_IsRefused()
    {
        var pdf = BuildPdf(doc => doc.GetCatalog().GetNameTree(PdfName.JavaScript)
            .AddEntry("init", PdfAction.CreateJavaScript("app.alert('hi')").GetPdfObject()));
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void LaunchOnLink_IsRefused()
    {
        var pdf = BuildPdf(doc =>
        {
            var link = new PdfLinkAnnotation(new Rectangle(50, 50, 100, 30))
                .SetAction(PdfAction.CreateLaunch(new PdfStringFS("cmd.exe")));
            doc.GetFirstPage().AddAnnotation(link);
        });
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void JavaScriptOnLink_IsRefused()
    {
        var pdf = BuildPdf(doc =>
        {
            var link = new PdfLinkAnnotation(new Rectangle(50, 50, 100, 30))
                .SetAction(PdfAction.CreateJavaScript("this.print()"));
            doc.GetFirstPage().AddAnnotation(link);
        });
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void JavaScriptChainedBehindGoTo_IsRefused()
    {
        var pdf = BuildPdf(doc =>
        {
            var goTo = PdfAction.CreateGoTo("top");
            goTo.Next(PdfAction.CreateJavaScript("app.alert(1)"));
            doc.GetCatalog().SetOpenAction(goTo);
        });
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void FormFieldScript_IsAllowed()
    {
        var pdf = BuildPdf(doc =>
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, "amount")
                .SetWidgetRectangle(new Rectangle(50, 700, 200, 20))
                .CreateText();
            field.SetAdditionalAction(PdfName.F, PdfAction.CreateJavaScript("AFNumber_Format(2, 0, 0, 0, '', true);"));
            form.AddField(field);
        });
        CreateGuard().EnsureSafePdf(pdf, "test");
    }

    [Fact]
    public void FormFieldLaunch_IsStillRefused()
    {
        var pdf = BuildPdf(doc =>
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, "evil")
                .SetWidgetRectangle(new Rectangle(50, 700, 200, 20))
                .CreateText();
            field.SetAdditionalAction(PdfName.F, PdfAction.CreateLaunch(new PdfStringFS("calc.exe")));
            form.AddField(field);
        });
        var ex = Assert.Throws<ApiValidationException>(() => CreateGuard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", ex.Code);
    }

    [Fact]
    public void EmbeddedFile_IsAllowed()
    {
        var pdf = BuildPdf(doc =>
        {
            var spec = PdfFileSpec.CreateEmbeddedFileSpec(doc, "{\"evidence\":true}"u8.ToArray(), "evidence.json", "evidence.json", null, null);
            doc.AddFileAttachment("evidence.json", spec);
        });
        CreateGuard().EnsureSafePdf(pdf, "test");
    }

    [Fact]
    public void ActiveContentCheck_CanBeTurnedOff()
    {
        var pdf = BuildPdf(doc => doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript("app.alert('hi')")));
        CreateGuard(new LimitsOptions { RejectActiveContent = false }).EnsureSafePdf(pdf, "test");
    }
}
