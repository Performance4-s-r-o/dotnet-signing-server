using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Checkbox field: ticked draws a check mark inside the rect, unticked draws nothing.
/// The portal decides visibility and required-ness; the engine only renders the value.
/// </summary>
public class CheckboxFieldTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly PdfTemplateService _service;

    private static readonly SignRect Rect = new() { X = 100, Y = 500, Width = 20, Height = 20 };

    public CheckboxFieldTests()
    {
        _dbContext = TestHelpers.CreateInMemoryDbContext();
        var limitGuard = new ContentLimitGuard(TestHelpers.WrapOptions(new LimitsOptions()));
        _service = new PdfTemplateService(_dbContext, NullLogger<PdfTemplateService>.Instance, limitGuard);
    }

    public void Dispose() => _dbContext.Dispose();

    /// <summary>Collects the bounding box of every stroked path on a page.</summary>
    private sealed class StrokedPaths : IEventListener
    {
        public List<Rectangle> Boxes { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_PATH) return;
            var info = (PathRenderInfo)data;
            if ((info.GetOperation() & PathRenderInfo.STROKE) == 0) return;
            var points = info.GetPath().GetSubpaths()
                .SelectMany(s => s.GetPiecewiseLinearApproximation())
                .Select(p => info.GetCtm().Multiply(new Matrix((float)p.GetX(), (float)p.GetY())))
                .ToList();
            if (points.Count == 0) return;
            var xs = points.Select(m => m.Get(Matrix.I31)).ToList();
            var ys = points.Select(m => m.Get(Matrix.I32)).ToList();
            Boxes.Add(new Rectangle(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_PATH };
    }

    private static List<Rectangle> StrokesOn(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        var listener = new StrokedPaths();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(1));
        return listener.Boxes;
    }

    private static PdfFieldDefinition Checkbox(string name = "consent") => new()
    {
        FieldName = name,
        Type = PdfFieldType.Checkbox,
        Rect = Rect,
        Page = 1,
    };

    private async Task<byte[]> FillWith(string? value)
    {
        var result = await _service.FillAsync(new FillPdfInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition> { Checkbox() },
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "consent", Value = value } } },
            },
        }, Guid.NewGuid());
        return Convert.FromBase64String(result.Files[0]);
    }

    [Fact]
    public void Checkbox_DeserialisesFromWireName()
    {
        var def = JsonSerializer.Deserialize<PdfFieldDefinition>(
            """{"fieldName":"consent","type":"checkbox","rect":{"x":1,"y":2,"width":3,"height":4}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(PdfFieldType.Checkbox, def!.Type);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("ano", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCheckboxChecked_AcceptsTheSameValuesAsThePortal(string? value, bool expected)
    {
        Assert.Equal(expected, PdfTemplateService.IsCheckboxChecked(value));
    }

    [Fact]
    public async Task FillAsync_Ticked_DrawsMarkInsideTheRect()
    {
        var strokes = StrokesOn(await FillWith("true"));

        var mark = Assert.Single(strokes);
        Assert.InRange(mark.GetLeft(), Rect.X, Rect.X + Rect.Width);
        Assert.InRange(mark.GetRight(), Rect.X, Rect.X + Rect.Width);
        Assert.InRange(mark.GetBottom(), Rect.Y, Rect.Y + Rect.Height);
        Assert.InRange(mark.GetTop(), Rect.Y, Rect.Y + Rect.Height);
    }

    [Fact]
    public async Task FillAsync_Unticked_DrawsNothing()
    {
        Assert.Empty(StrokesOn(await FillWith("false")));
    }

    [Fact]
    public void StampTextFields_Ticked_DrawsMark()
    {
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput>
        {
            new() { Value = "true", Definition = Checkbox() },
        });

        Assert.Single(StrokesOn(stamped));
    }

    [Fact]
    public void StampTextFields_Unticked_LeavesTheDocumentUntouched()
    {
        // Append mode would still add a revision; an unticked box must not.
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
        var stamped = PdfTemplateService.StampTextFields(pdf, new List<PreSignFieldInput>
        {
            new() { Value = "false", Definition = Checkbox() },
        });

        Assert.Equal(pdf, stamped);
    }

    [Fact]
    public async Task CreateTemplate_AcceptsCheckboxField()
    {
        var created = await _service.CreateTemplateAsync(new CreateTemplateInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition> { Checkbox() },
        }, Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, created.TemplateId);
    }
}
