using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Proves the cross-surface invariant: a field placed at a given PDF SignRect
/// (bottom-left origin, points) is RENDERED at that rect by the .NET engine, and
/// the SIGNING stamping path (<see cref="PdfTemplateService.StampTextFields"/>) and
/// the TEMPLATE/fill path (<see cref="PdfTemplateService.FillAsync"/>) place the
/// same text at the SAME position — so "where the creator dropped it" == "where the
/// PDF shows it", regardless of which surface produced the request.
///
/// Both SPFx surfaces (signing useSignRect→rectToSignRect/buildPresignBody, template
/// builder mapFieldToTemplate) and the v0 toSignRect all converge on PDF-point
/// bottom-left rects, then feed the SAME AddText here. These tests pin the engine end.
/// </summary>
public class FieldPlacementConsistencyTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly PdfTemplateService _service;

    public FieldPlacementConsistencyTests()
    {
        _dbContext = TestHelpers.CreateInMemoryDbContext();
        var limitGuard = new ContentLimitGuard(
            TestHelpers.WrapOptions(new DotNetSigningServer.Options.LimitsOptions()));
        _service = new PdfTemplateService(
            _dbContext,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfTemplateService>.Instance,
            limitGuard);
    }

    public void Dispose() => _dbContext.Dispose();

    /// <summary>Captures the baseline start point of the first text chunk rendered on a page.</summary>
    private sealed class FirstTextPosition : IEventListener
    {
        public Vector? Start { get; private set; }
        public string Text { get; private set; } = "";

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || Start != null) return;
            var info = (TextRenderInfo)data;
            if (string.IsNullOrEmpty(info.GetText())) return;
            Start = info.GetBaseline().GetStartPoint();
            Text = info.GetText();
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }

    private static (float x, float y, string text) ExtractFirstText(byte[] pdf, int page = 1)
    {
        using var ms = new MemoryStream(pdf);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        var listener = new FirstTextPosition();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(page));
        Assert.NotNull(listener.Start);
        return (listener.Start!.Get(0), listener.Start.Get(1), listener.Text);
    }

    // A4 minimal page is 595 x 842 pt. Rect chosen mid-page so left-aligned/top
    // text lands inside the box with room to spare.
    private static readonly SignRect Rect = new() { X = 120, Y = 600, Width = 250, Height = 40 };
    private const string Value = "PlacementProbe";

    [Fact]
    public void StampTextFields_PlacesTextInsideTheRect()
    {
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput>
        {
            new()
            {
                Value = Value,
                Definition = new PdfFieldDefinition
                {
                    FieldName = "probe",
                    Page = 1,
                    Rect = Rect,
                    HorizontalAlign = PdfHorizontalAlign.Left,
                    VerticalAlign = PdfVerticalAlign.Top,
                },
            },
        });

        var (x, y, text) = ExtractFirstText(stamped);
        Assert.Equal(Value, text);
        // X starts at the left edge of the rect (allow a sub-point fudge for glyph side bearing).
        Assert.InRange(x, Rect.X - 1f, Rect.X + 2f);
        // Y baseline sits within the rect's vertical band (bottom-left origin).
        Assert.InRange(y, Rect.Y - 1f, Rect.Y + Rect.Height + 1f);
    }

    [Fact]
    public async Task SigningStamp_And_TemplateFill_PlaceTextIdentically()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();

        // SIGNING path: StampTextFields with a PreSignFieldInput.
        var stamped = PdfTemplateService.StampTextFields(
            Convert.FromBase64String(pdfBase64),
            new List<PreSignFieldInput>
            {
                new()
                {
                    Value = Value,
                    Definition = new PdfFieldDefinition
                    {
                        FieldName = "probe",
                        Page = 1,
                        Rect = Rect,
                        HorizontalAlign = PdfHorizontalAlign.Left,
                        VerticalAlign = PdfVerticalAlign.Top,
                    },
                },
            });

        // TEMPLATE/FILL path: same field def + value through FillAsync → AddText.
        var fillResult = await _service.FillAsync(
            new FillPdfInput
            {
                PdfContent = pdfBase64,
                Fields = new List<PdfFieldDefinition>
                {
                    new()
                    {
                        FieldName = "probe",
                        Page = 1,
                        Rect = Rect,
                        HorizontalAlign = PdfHorizontalAlign.Left,
                        VerticalAlign = PdfVerticalAlign.Top,
                    },
                },
                Data = new List<FillDataSet>
                {
                    new() { Data = new List<PdfFieldValue> { new() { FieldName = "probe", Value = Value } } },
                },
            },
            Guid.NewGuid());

        var (sx, sy, stext) = ExtractFirstText(stamped);
        var (fx, fy, ftext) = ExtractFirstText(Convert.FromBase64String(fillResult.Files[0]));

        Assert.Equal(Value, stext);
        Assert.Equal(Value, ftext);
        // The two surfaces must land the text at the same spot (sub-point tolerance).
        Assert.InRange(Math.Abs(sx - fx), 0f, 0.5f);
        Assert.InRange(Math.Abs(sy - fy), 0f, 0.5f);
    }

    [Fact]
    public void StampTextFields_WithAlphaColorsAndRadius_RendersTextUnchangedPosition()
    {
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

        // 8-digit hex (alpha) on bg/text/border + a corner radius must not throw
        // and must not move the text — placement is independent of decoration.
        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput>
        {
            new()
            {
                Value = Value,
                Definition = new PdfFieldDefinition
                {
                    FieldName = "decorated",
                    Page = 1,
                    Rect = Rect,
                    HorizontalAlign = PdfHorizontalAlign.Left,
                    VerticalAlign = PdfVerticalAlign.Top,
                    TextColor = "#FF000080",        // 50% red
                    BackgroundColor = "#FFFF0040",  // ~25% yellow
                    BorderStyle = "solid",
                    BorderWidth = 2,
                    BorderColor = "#0000FFCC",      // ~80% blue
                    BorderRadius = 8,
                },
            },
        });

        var (x, y, text) = ExtractFirstText(stamped);
        Assert.Equal(Value, text);
        Assert.InRange(x, Rect.X - 1f, Rect.X + 2f);
        Assert.InRange(y, Rect.Y - 1f, Rect.Y + Rect.Height + 1f);
        // Decoration actually emitted content (bigger than a bare-text stamp).
        Assert.True(stamped.Length > pdf.Length);
    }

    /// <summary>
    /// A 1x1 transparent PNG as base64 — the smallest valid PNG that iText can decode.
    /// </summary>
    private const string MinimalPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void StampTextFields_StampsImageField()
    {
        // Arrange: a PreSignFieldInput with Type = Image and a valid tiny base64 PNG.
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
        var field = new PreSignFieldInput
        {
            Value = MinimalPngBase64,
            Definition = new PdfFieldDefinition
            {
                FieldName = "img_field",
                Type = PdfFieldType.Image,
                Page = 1,
                Rect = Rect,
            },
        };

        // Act
        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput> { field });

        // Assert 1: no exception + output bytes differ from input (image content was written).
        Assert.NotEqual(pdf, stamped);

        // Assert 2: the stamped page carries at least one image XObject in its resources.
        using var ms = new MemoryStream(stamped);
        using var reader = new iText.Kernel.Pdf.PdfReader(ms);
        using var doc = new iText.Kernel.Pdf.PdfDocument(reader);
        var page = doc.GetPage(1);
        var resources = page.GetResources();
        // Collect XObjects from all content streams (append mode may add incremental revisions).
        bool hasImageXObject = false;
        var xObjDict = resources.GetResource(iText.Kernel.Pdf.PdfName.XObject);
        if (xObjDict != null)
        {
            foreach (var key in xObjDict.KeySet())
            {
                var xobj = xObjDict.GetAsStream(key);
                if (xobj != null)
                {
                    var subtype = xobj.GetAsName(iText.Kernel.Pdf.PdfName.Subtype);
                    if (iText.Kernel.Pdf.PdfName.Image.Equals(subtype))
                    {
                        hasImageXObject = true;
                        break;
                    }
                }
            }
        }
        Assert.True(hasImageXObject, "Expected at least one /Image XObject on page 1 after stamping an image field.");
    }

    [Theory]
    [InlineData(PdfHorizontalAlign.Left)]
    [InlineData(PdfHorizontalAlign.Center)]
    [InlineData(PdfHorizontalAlign.Right)]
    public void HorizontalAlign_KeepsTextWithinRectWidth(PdfHorizontalAlign align)
    {
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput>
        {
            new()
            {
                Value = "AlignProbe",
                Definition = new PdfFieldDefinition
                {
                    FieldName = "a", Page = 1, Rect = Rect, HorizontalAlign = align,
                    VerticalAlign = PdfVerticalAlign.Top,
                },
            },
        });

        var (x, _, _) = ExtractFirstText(stamped);
        // Whatever the alignment, the text's start X stays within the rect's horizontal span.
        Assert.InRange(x, Rect.X - 1f, Rect.X + Rect.Width + 1f);
    }
}
