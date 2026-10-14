using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#70: how many documents, fields and bytes one fill request may produce.</summary>
public class FillLimitsTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext = TestHelpers.CreateInMemoryDbContext();

    public void Dispose() => _dbContext.Dispose();

    private PdfTemplateService Service(Action<LimitsOptions>? tweak = null)
    {
        var options = new LimitsOptions();
        tweak?.Invoke(options);
        var guard = new ContentLimitGuard(TestHelpers.WrapOptions(options));
        return new PdfTemplateService(_dbContext, NullLogger<PdfTemplateService>.Instance, guard, new KeyEchoLocalizerFactory());
    }

    private static PdfFieldDefinition Text(string name) => new()
    {
        FieldName = name,
        Type = PdfFieldType.Text,
        Rect = new SignRect { X = 50, Y = 700, Width = 100, Height = 20 },
        Page = 1,
    };

    private static FillPdfInput Input(int documents, IEnumerable<PdfFieldDefinition>? fields = null) => new()
    {
        PdfContent = TestHelpers.CreateMinimalPdfBase64(),
        Fields = (fields ?? new[] { Text("name") }).ToList(),
        Data = Enumerable.Range(0, documents)
            .Select(_ => new FillDataSet { Data = new List<PdfFieldValue> { new() { FieldName = "name", Value = "x" } } })
            .ToList(),
    };

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ApiValidationException>(act)).Code;

    [Fact]
    public async Task Too_many_documents_are_refused_before_any_is_generated()
    {
        var sut = Service(o => o.FillMaxDocuments = 3);
        Assert.Equal("FILL_TOO_MANY_DOCUMENTS", await CodeOf(() => sut.FillAsync(Input(4), Guid.NewGuid())));

        var ok = await sut.FillAsync(Input(3), Guid.NewGuid());
        Assert.Equal(3, ok.Files.Count);
    }

    [Fact]
    public async Task Too_many_fields_are_refused()
    {
        var sut = Service(o => o.FillMaxFields = 2);
        var fields = Enumerable.Range(0, 3).Select(i => Text(i == 0 ? "name" : $"f{i}"));
        Assert.Equal("FILL_TOO_MANY_FIELDS", await CodeOf(() => sut.FillAsync(Input(1, fields), Guid.NewGuid())));
    }

    [Fact]
    public async Task Output_over_the_total_limit_stops_generation()
    {
        var sut = Service(o => o.FillMaxOutputBytes = 1000);
        Assert.Equal("FILL_OUTPUT_TOO_LARGE", await CodeOf(() => sut.FillAsync(Input(5), Guid.NewGuid())));
    }

    [Fact]
    public async Task An_image_value_over_the_pixel_limit_is_refused_by_its_header()
    {
        var sut = Service();
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x75, 0x30, 0, 0, 0x75, 0x30, 8, 6, 0, 0, 0 }; // 30 000 x 30 000
        var input = Input(1, new[] { new PdfFieldDefinition
        {
            FieldName = "photo",
            Type = PdfFieldType.Image,
            Rect = new SignRect { X = 50, Y = 500, Width = 100, Height = 100 },
            Page = 1,
        } });
        input.Data[0].Data = new List<PdfFieldValue> { new() { FieldName = "photo", Value = Convert.ToBase64String(png.ToArray()) } };

        Assert.Equal("IMAGE_DIMENSIONS_EXCEEDED", await CodeOf(() => sut.FillAsync(input, Guid.NewGuid())));
    }
}
