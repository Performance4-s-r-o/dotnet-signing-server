using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.IO.Font;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Pdf.Navigation;
using iText.Signatures;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #180: form flattening before signing. Every fixture is built here with iText — no binary
/// files in the repository. What matters: values end up on the page (with diacritics), active
/// content is gone so the guard accepts the result with RejectActiveContent on, empty signature
/// fields and URI links stay, and a signed document is refused before anything changes.
/// </summary>
public class PdfFormFlattenerTests
{
    private static readonly LimitsOptions Limits = new();
    private const string Czech = "Příliš žluťoučký kůň úpěl ďábelské ódy";

    private static PdfSafetyGuard Guard() => new(TestHelpers.WrapOptions(new LimitsOptions { RejectActiveContent = true }));

    private static byte[] Build(Action<PdfDocument> configure, bool tagged = false)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            if (tagged) doc.SetTagged();
            doc.AddNewPage(PageSize.A4);
            configure(doc);
        }
        return ms.ToArray();
    }

    private static PdfFormField Text(PdfDocument doc, string name, string value, float y = 700)
    {
        var form = PdfAcroForm.GetAcroForm(doc, true);
        var field = new TextFormFieldBuilder(doc, name)
            .SetWidgetRectangle(new Rectangle(50, y, 400, 24)).SetPage(1).CreateText();
        field.SetValue(value);
        form.AddField(field);
        return field;
    }

    private static PdfFormField SignatureField(PdfDocument doc, string name)
    {
        var form = PdfAcroForm.GetAcroForm(doc, true);
        var field = new SignatureFormFieldBuilder(doc, name)
            .SetWidgetRectangle(new Rectangle(50, 100, 200, 50)).SetPage(1).CreateSignature();
        form.AddField(field);
        return field;
    }

    private static string PageText(byte[] pdf, int page = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(page));
    }

    private static T Read<T>(byte[] pdf, Func<PdfDocument, T> read)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return read(doc);
    }

    private static PdfFormFlattener.Result Flatten(byte[] pdf)
    {
        var result = PdfFormFlattener.Flatten(pdf, Limits);
        // Whatever comes out must pass the full check, active content included.
        Guard().EnsureSafePdf(result.Pdf, "test");
        return result;
    }

    // ── fields ───────────────────────────────────────────────────────────

    [Fact]
    public void Filled_fields_become_page_content_and_the_form_goes()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "name", "Jane Roe");
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var checkedBox = new CheckBoxFormFieldBuilder(doc, "agree")
                .SetWidgetRectangle(new Rectangle(50, 600, 20, 20)).SetPage(1).CreateCheckBox();
            checkedBox.SetValue("Yes");
            form.AddField(checkedBox);
            var emptyBox = new CheckBoxFormFieldBuilder(doc, "spam")
                .SetWidgetRectangle(new Rectangle(100, 600, 20, 20)).SetPage(1).CreateCheckBox();
            form.AddField(emptyBox);
            var combo = new ChoiceFormFieldBuilder(doc, "city")
                .SetWidgetRectangle(new Rectangle(50, 500, 200, 24)).SetPage(1)
                .SetOptions(new[] { "Brno", "Praha" }).CreateComboBox();
            combo.SetValue("Praha");
            form.AddField(combo);
        });

        var result = Flatten(pdf);

        Assert.True(result.Report.Changed);
        Assert.Equal(4, result.Report.FlattenedFields);
        var text = PageText(result.Pdf);
        Assert.Contains("Jane Roe", text);
        Assert.Contains("Praha", text);
        Assert.Null(Read(result.Pdf, d => d.GetCatalog().GetPdfObject().Get(PdfName.AcroForm)));
        Assert.Equal(0, Read(result.Pdf, d => d.GetPage(1).GetAnnotsSize()));
    }

    [Fact]
    public void NeedAppearances_draws_the_value_not_the_stale_appearance()
    {
        var pdf = Build(doc =>
        {
            var field = Text(doc, "note", "old value");
            // A tool that writes /V only and asks the viewer to redraw.
            field.GetPdfObject().Put(PdfName.V, new PdfString("new value"));
            PdfAcroForm.GetAcroForm(doc, false).SetNeedAppearances(true);
        });

        var result = Flatten(pdf);

        var text = PageText(result.Pdf);
        Assert.Contains("new value", text);
        Assert.DoesNotContain("old value", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Czech_value_is_drawn_with_an_embedded_font(bool needAppearances)
    {
        var pdf = Build(doc =>
        {
            var field = Text(doc, "note", "x");
            field.GetPdfObject().Put(PdfName.V, new PdfString(Czech, PdfEncodings.UNICODE_BIG));
            if (needAppearances)
            {
                PdfAcroForm.GetAcroForm(doc, false).SetNeedAppearances(true);
            }
            else
            {
                // No appearance at all: iText would generate one in Helvetica (no č, ř, ů).
                field.GetPdfObject().Remove(PdfName.AP);
            }
        });

        var result = Flatten(pdf);

        Assert.Equal(Czech, PageText(result.Pdf).Trim());
    }

    [Fact]
    public void Existing_appearance_is_kept_without_NeedAppearances()
    {
        // What the user saw is the appearance; without NeedAppearances it is not redrawn.
        var pdf = Build(doc =>
        {
            var field = Text(doc, "note", "shown");
            field.GetPdfObject().Put(PdfName.V, new PdfString("hidden value"));
        });

        var text = PageText(Flatten(pdf).Pdf);
        Assert.Contains("shown", text);
    }

    [Fact]
    public void Hidden_field_is_not_drawn()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "visible", "on the page", y: 700);
            var hidden = Text(doc, "secret", "not on the page", y: 600);
            hidden.GetWidgets()[0].SetFlags(PdfAnnotation.HIDDEN);
        });

        var result = Flatten(pdf);

        var text = PageText(result.Pdf);
        Assert.Contains("on the page", text);
        Assert.DoesNotContain("not on the page", text);
        Assert.Null(Read(result.Pdf, d => d.GetCatalog().GetPdfObject().Get(PdfName.AcroForm)));
    }

    [Fact]
    public void Field_scripts_go_with_the_fields()
    {
        var pdf = Build(doc =>
        {
            var field = Text(doc, "amount", "12.50");
            field.SetAdditionalAction(PdfName.F, PdfAction.CreateJavaScript("AFNumber_Format(2, 0, 0, 0, '', true);"));
            field.SetAdditionalAction(PdfName.K, PdfAction.CreateLaunch(new PdfStringFS("calc.exe")));
        });

        var result = Flatten(pdf);

        Assert.Contains("12.50", PageText(result.Pdf));
        Assert.Null(Read(result.Pdf, d => d.GetCatalog().GetPdfObject().Get(PdfName.AcroForm)));
    }

    [Fact]
    public void Combo_display_text_with_diacritics_is_drawn_with_an_embedded_font()
    {
        var pdf = Build(doc =>
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var combo = new ChoiceFormFieldBuilder(doc, "country")
                .SetWidgetRectangle(new Rectangle(50, 500, 300, 24)).SetPage(1)
                .SetOptions(new[] { new[] { "CZ", "Česká republika" }, new[] { "SK", "Slovenská republika" } })
                .CreateComboBox();
            form.AddField(combo);
            combo.GetPdfObject().Put(PdfName.V, new PdfString("CZ"));
            form.SetNeedAppearances(true);
        });

        Assert.Contains("Česká republika", PageText(Flatten(pdf).Pdf));
    }

    [Fact]
    public void Field_in_a_layer_that_is_off_is_not_drawn()
    {
        var pdf = Build(doc =>
        {
            var layer = new iText.Kernel.Pdf.Layer.PdfLayer("hidden", doc);
            layer.SetOn(false);
            Text(doc, "visible", "on the page", y: 700);
            var hidden = Text(doc, "secret", "not on the page", y: 600);
            hidden.GetWidgets()[0].GetPdfObject().Put(PdfName.OC, layer.GetPdfObject());
        });

        var text = PageText(Flatten(pdf).Pdf);
        Assert.Contains("on the page", text);
        Assert.DoesNotContain("not on the page", text);
    }

    // ── active content ───────────────────────────────────────────────────

    [Fact]
    public void Document_scripts_and_script_links_go_and_a_URI_link_stays()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "name", "Jane Roe");
            var catalog = doc.GetCatalog();
            catalog.GetNameTree(PdfName.JavaScript).AddEntry("!ADBE::0200_VersChkCode_XFACheck",
                PdfAction.CreateJavaScript("app.alert('version')").GetPdfObject());
            catalog.SetOpenAction(PdfAction.CreateJavaScript("app.alert('open')"));
            catalog.SetAdditionalAction(PdfName.WC, PdfAction.CreateJavaScript("app.alert('close')"));
            var page = doc.GetFirstPage();
            page.SetAdditionalAction(PdfName.O, PdfAction.CreateJavaScript("app.alert('page')"));
            page.AddAnnotation(new PdfLinkAnnotation(new Rectangle(50, 300, 100, 20))
                .SetAction(PdfAction.CreateJavaScript("app.alert('link')")));
            page.AddAnnotation(new PdfLinkAnnotation(new Rectangle(50, 250, 100, 20))
                .SetAction(PdfAction.CreateURI("https://example.com/")));
        });
        var refused = Assert.Throws<ApiValidationException>(() => Guard().EnsureSafePdf(pdf, "test"));
        Assert.Equal("PDF_ACTIVE_CONTENT", refused.Code);

        var result = Flatten(pdf);

        Assert.Equal(
            new[] { "annotation-actions", "document-actions", "document-javascript", "open-action", "page-actions" },
            result.Report.Removed);
        Read(result.Pdf, d =>
        {
            var catalog = d.GetCatalog().GetPdfObject();
            Assert.Null(catalog.GetAsDictionary(PdfName.Names)?.Get(PdfName.JavaScript));
            Assert.Null(catalog.Get(PdfName.OpenAction));
            Assert.Null(catalog.Get(PdfName.AA));
            Assert.Null(d.GetPage(1).GetPdfObject().Get(PdfName.AA));
            var links = d.GetPage(1).GetAnnotations().OfType<PdfLinkAnnotation>().ToList();
            Assert.Equal(2, links.Count);
            Assert.Null(links[0].GetAction());
            Assert.Equal("https://example.com/", links[1].GetAction().GetAsString(PdfName.URI).ToUnicodeString());
            return 0;
        });
    }

    [Fact]
    public void Launch_in_a_Next_chain_goes_and_the_link_keeps_its_URI()
    {
        var pdf = Build(doc =>
        {
            var action = PdfAction.CreateURI("https://example.com/");
            action.Next(PdfAction.CreateLaunch(new PdfStringFS("calc.exe")));
            doc.GetFirstPage().AddAnnotation(new PdfLinkAnnotation(new Rectangle(50, 300, 100, 20)).SetAction(action));
        });

        var result = Flatten(pdf);

        Assert.Equal(new[] { "annotation-actions" }, result.Report.Removed);
        Read(result.Pdf, d =>
        {
            var link = d.GetPage(1).GetAnnotations().OfType<PdfLinkAnnotation>().Single();
            Assert.Equal(PdfName.URI, link.GetAction().GetAsName(PdfName.S));
            Assert.Null(link.GetAction().Get(PdfName.Next));
            return 0;
        });
    }

    [Fact]
    public void GoTo_open_action_stays()
    {
        var pdf = Build(doc => doc.GetCatalog().SetOpenAction(PdfExplicitDestination.CreateFit(doc.GetFirstPage())));

        var result = Flatten(pdf);

        Assert.False(result.Report.Changed);
        Assert.Same(pdf, result.Pdf);
    }

    [Fact]
    public void Bookmark_script_goes()
    {
        var pdf = Build(doc =>
        {
            var root = doc.GetOutlines(false);
            root.AddOutline("run").AddAction(PdfAction.CreateJavaScript("app.alert('outline')"));
        });

        var result = Flatten(pdf);

        Assert.Equal(new[] { "outline-actions" }, result.Report.Removed);
        Read(result.Pdf, d =>
        {
            var item = d.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Outlines)!.GetAsDictionary(PdfName.First)!;
            Assert.Null(item.Get(PdfName.A));
            return 0;
        });
    }

    [Fact]
    public void Script_only_document_without_fields_is_cleaned_and_keeps_its_comment()
    {
        // The shape of the document from #180: an empty AcroForm, a JavaScript name tree, a comment.
        var pdf = Build(doc =>
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            form.SetDefaultAppearance("/Helv 0 Tf 0 g");
            doc.GetCatalog().GetNameTree(PdfName.JavaScript).AddEntry("init",
                PdfAction.CreateJavaScript("app.alert('hi')").GetPdfObject());
            doc.GetFirstPage().AddAnnotation(new PdfFreeTextAnnotation(new Rectangle(50, 400, 200, 40), new PdfString("note")));
        });

        var result = Flatten(pdf);

        Assert.Equal(0, result.Report.FlattenedFields);
        Assert.Equal(new[] { "document-javascript" }, result.Report.Removed);
        Assert.Equal(1, Read(result.Pdf, d => d.GetPage(1).GetAnnotations().OfType<PdfFreeTextAnnotation>().Count()));
    }

    [Fact]
    public void Clean_document_comes_back_unchanged()
    {
        var pdf = Build(_ => { });

        var result = Flatten(pdf);

        Assert.False(result.Report.Changed);
        Assert.Equal(0, result.Report.FlattenedFields);
        Assert.Empty(result.Report.Removed);
        Assert.Same(pdf, result.Pdf);
    }

    [Fact]
    public void Reader_extensions_go()
    {
        var pdf = Build(doc =>
        {
            var perms = new PdfDictionary();
            perms.Put(new PdfName("UR3"), new PdfDictionary());
            doc.GetCatalog().Put(PdfName.Perms, perms);
        });

        var result = Flatten(pdf);

        Assert.Equal(new[] { "reader-extensions" }, result.Report.Removed);
        Assert.Null(Read(result.Pdf, d => d.GetCatalog().GetPdfObject().Get(PdfName.Perms)));
    }

    // ── signature fields and signatures ──────────────────────────────────

    [Fact]
    public void Empty_signature_field_stays_without_its_actions()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "name", "Jane Roe");
            var sig = SignatureField(doc, "Signature1");
            sig.SetAdditionalAction(PdfName.F, PdfAction.CreateJavaScript("app.alert('sig')"));
            sig.GetWidgets()[0].SetAdditionalAction(PdfName.E, PdfAction.CreateJavaScript("app.alert('enter')"));
        });

        var result = Flatten(pdf);

        Assert.Equal(1, result.Report.FlattenedFields);
        Assert.Contains("field-actions", result.Report.Removed);
        Assert.Contains("Jane Roe", PageText(result.Pdf));
        Read(result.Pdf, d =>
        {
            var form = PdfAcroForm.GetAcroForm(d, false);
            Assert.NotNull(form);
            var fields = form!.GetAllFormFields();
            Assert.Equal(new[] { "Signature1" }, fields.Keys.ToArray());
            var sig = fields["Signature1"];
            Assert.Equal(PdfName.Sig, sig.GetFormType());
            Assert.Null(sig.GetPdfObject().Get(PdfName.AA));
            Assert.Single(d.GetPage(1).GetAnnotations());
            return 0;
        });
    }

    [Fact]
    public void Only_signature_fields_are_left_alone()
    {
        // Without a list iText flattens every field: the empty signature field must survive.
        var pdf = Build(doc => SignatureField(doc, "Signature1"));

        var result = Flatten(pdf);

        Assert.Equal(0, result.Report.FlattenedFields);
        Assert.Equal(new[] { "Signature1" },
            Read(result.Pdf, d => PdfAcroForm.GetAcroForm(d, false)!.GetAllFormFields().Keys.ToArray()));
    }

    [Fact]
    public void Signed_document_is_refused_before_any_change()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var service = new PdfSigningService(TestHelpers.WrapOptions(new EvidenceOptions()), new PdfVisualSigningService());
        var signed = Convert.FromBase64String(service.SignWithPfx(new PfxSignInput
        {
            PdfContent = Convert.ToBase64String(Build(doc => Text(doc, "name", "Jane Roe"))),
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature1",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        }));
        var copy = (byte[])signed.Clone();

        var ex = Assert.Throws<ApiValidationException>(() => PdfFormFlattener.Flatten(signed, Limits));

        Assert.Equal(PdfFormFlattener.AlreadySignedCode, ex.Code);
        Assert.Equal(copy, signed);
    }

    [Fact]
    public void Signature_value_under_an_inherited_FT_is_refused()
    {
        // SignatureUtil misses it (no /FT of its own); the rewrite would still break it.
        var pdf = Build(doc =>
        {
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var parent = new PdfDictionary();
            parent.Put(PdfName.FT, PdfName.Sig);
            parent.Put(PdfName.T, new PdfString("sigs"));
            var kid = new PdfDictionary();
            kid.Put(PdfName.T, new PdfString("one"));
            kid.Put(PdfName.Parent, parent.MakeIndirect(doc));
            var value = new PdfDictionary();
            value.Put(PdfName.Type, PdfName.Sig);
            kid.Put(PdfName.V, value);
            parent.Put(PdfName.Kids, new PdfArray(kid.MakeIndirect(doc)));
            form.GetPdfObject().Put(PdfName.Fields, new PdfArray(parent));
        });

        var ex = Assert.Throws<ApiValidationException>(() => PdfFormFlattener.Flatten(pdf, Limits));
        Assert.Equal(PdfFormFlattener.AlreadySignedCode, ex.Code);
    }

    [Fact]
    public void Flattened_document_signs_and_the_signature_verifies()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "name", Czech);
            PdfAcroForm.GetAcroForm(doc, false).SetNeedAppearances(true);
            SignatureField(doc, "Signature1");
            doc.GetCatalog().GetNameTree(PdfName.JavaScript).AddEntry("init",
                PdfAction.CreateJavaScript("app.alert('hi')").GetPdfObject());
        });
        var flattened = Flatten(pdf).Pdf;

        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var service = new PdfSigningService(TestHelpers.WrapOptions(new EvidenceOptions()), new PdfVisualSigningService());
        var signed = Convert.FromBase64String(service.SignWithPfx(new PfxSignInput
        {
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature2",
            SignRect = new SignRect { X = 300, Y = 10, Width = 200, Height = 50 },
        }, flattened));

        Guard().EnsureSafePdf(signed, "test");
        Read(signed, d =>
        {
            var util = new SignatureUtil(d);
            var name = Assert.Single(util.GetSignatureNames());
            Assert.True(util.ReadSignatureData(name).VerifySignatureIntegrityAndAuthenticity());
            Assert.True(util.SignatureCoversWholeDocument(name));
            return 0;
        });
        Assert.Contains(Czech, PageText(signed));
    }

    // ── XFA and tags ─────────────────────────────────────────────────────

    private static void PutXfa(PdfDocument doc)
    {
        var xfa = new PdfStream(System.Text.Encoding.UTF8.GetBytes(
            "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\"/></template>"
            + "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1/></xfa:data></xfa:datasets>"
            + "</xdp:xdp>"));
        PdfAcroForm.GetAcroForm(doc, true).GetPdfObject().Put(PdfName.XFA, xfa.MakeIndirect(doc));
    }

    [Fact]
    public void Hybrid_XFA_loses_the_XFA_and_is_flattened()
    {
        var pdf = Build(doc =>
        {
            Text(doc, "name", "Jane Roe");
            PutXfa(doc);
        });

        var result = Flatten(pdf);

        Assert.Contains("xfa", result.Report.Removed);
        Assert.Contains("Jane Roe", PageText(result.Pdf));
        Assert.Null(Read(result.Pdf, d => d.GetCatalog().GetPdfObject().Get(PdfName.AcroForm)));
    }

    [Fact]
    public void Dynamic_XFA_is_refused()
    {
        var withFlag = Build(doc =>
        {
            Text(doc, "stub", "Please wait...");
            PutXfa(doc);
            doc.GetCatalog().Put(new PdfName("NeedsRendering"), PdfBoolean.TRUE);
        });
        var withoutWidgets = Build(PutXfa);

        foreach (var pdf in new[] { withFlag, withoutWidgets })
        {
            var ex = Assert.Throws<ApiValidationException>(() => PdfFormFlattener.Flatten(pdf, Limits));
            Assert.Equal(PdfFormFlattener.XfaDynamicCode, ex.Code);
        }
    }

    [Fact]
    public void Tagged_form_stays_tagged_and_opens()
    {
        var pdf = Build(doc => Text(doc, "name", "Jane Roe"), tagged: true);

        var result = Flatten(pdf);

        Assert.True(Read(result.Pdf, d => d.IsTagged()));
        Assert.Contains("Jane Roe", PageText(result.Pdf));
    }
}
