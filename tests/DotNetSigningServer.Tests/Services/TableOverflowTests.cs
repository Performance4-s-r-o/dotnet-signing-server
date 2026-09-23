using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Tabulka, která se do svého pole nevejde.
///
/// Dřív se celá poslala do plátna omezeného obdélníkem pole a iText zbytek
/// mlčky uřízl — bez chyby, bez logu. Padesát řádků do pole na osm znamenalo
/// čtyřicet dva zmizelých řádků a dokument, který vypadal hotově. Testy hlídají
/// to jediné, na čem záleží: **žádný řádek se neztratí**.
/// </summary>
public class TableOverflowTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly PdfTemplateService _service;

    public TableOverflowTests()
    {
        _dbContext = TestHelpers.CreateInMemoryDbContext();
        var limitGuard = new ContentLimitGuard(TestHelpers.WrapOptions(new LimitsOptions()));
        _service = new PdfTemplateService(
            _dbContext, NullLogger<PdfTemplateService>.Instance, limitGuard, new KeyEchoLocalizerFactory());
    }

    public void Dispose() => _dbContext.Dispose();

    private static PdfFieldDefinition TableField(float height, int? sortColumn = null, bool desc = false) => new()
    {
        FieldName = "items",
        Type = PdfFieldType.Table,
        Rect = new SignRect { X = 40, Y = 600, Width = 300, Height = height },
        Page = 1,
        FontSize = 9,
        SortColumn = sortColumn,
        SortDescending = desc,
        TableColumns = new List<TableColumnDefinition>
        {
            new() { Name = "Polozka", WidthPercent = 60, FontSize = 9 },
            new() { Name = "Cena", WidthPercent = 40, FontSize = 9 },
        },
    };

    private async Task<byte[]> Fill(float boxHeight, int rowCount)
    {
        var rows = Enumerable.Range(1, rowCount)
            .Select(i => new List<string> { $"Radek-{i}", $"{i}00" })
            .ToList();
        return await FillRows(boxHeight, rows);
    }

    private async Task<byte[]> FillRows(
        float boxHeight, List<List<string>> rows, int? sortColumn = null, bool desc = false)
    {

        var result = await _service.FillAsync(new FillPdfInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition> { TableField(boxHeight, sortColumn, desc) },
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "items", TableValue = rows } } },
            },
        }, Guid.NewGuid());
        return Convert.FromBase64String(result.Files[0]);
    }

    private static (int Pages, string Text) Read(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var text = string.Empty;
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            text += PdfTextExtractor.GetTextFromPage(doc.GetPage(p), new SimpleTextExtractionStrategy());
        }
        return (doc.GetNumberOfPages(), text);
    }

    [Fact]
    public async Task A_table_that_fits_stays_on_its_page()
    {
        var (pages, text) = Read(await Fill(boxHeight: 200, rowCount: 5));

        Assert.Equal(1, pages);
        for (int i = 1; i <= 5; i++) Assert.Contains($"Radek-{i}", text);
        Assert.DoesNotContain("GeneratedTableTitle", text);
    }

    [Fact]
    public async Task No_row_is_lost_when_the_table_does_not_fit()
    {
        const int rows = 60;
        var (pages, text) = Read(await Fill(boxHeight: 40, rowCount: rows));

        // Dokument vyrostl o stránky s pokračováním.
        Assert.True(pages > 1, "expected at least one appended page");
        // A tohle je celý smysl opravy.
        for (int i = 1; i <= rows; i++)
        {
            Assert.Contains($"Radek-{i}", text);
        }
    }

    [Fact]
    public async Task The_shortened_table_says_where_the_rest_is()
    {
        var (_, text) = Read(await Fill(boxHeight: 40, rowCount: 60));

        // Lokalizátor v testech vrací klíč — stačí, že je věta v dokumentu.
        Assert.Contains("TableContinuesOnGeneratedPage", text);
        Assert.Contains("GeneratedTableTitle", text);
    }

    [Fact]
    public async Task Rows_are_sorted_by_the_chosen_column()
    {
        var rows = new List<List<string>>
        {
            new() { "Kolo", "9" },
            new() { "Auto", "10" },
            new() { "Lod", "2" },
        };
        // Podle druhého sloupce vzestupně: 2, 9, 10 — ne 10, 2, 9, jak by
        // dopadlo porovnání řetězců.
        var (_, text) = Read(await FillRows(200, rows, sortColumn: 2));
        Assert.True(text.IndexOf("Lod") < text.IndexOf("Kolo"));
        Assert.True(text.IndexOf("Kolo") < text.IndexOf("Auto"));
    }

    [Fact]
    public async Task Empty_values_go_last_whichever_way_it_is_sorted()
    {
        var rows = new List<List<string>>
        {
            new() { "Bez ceny", "" },
            new() { "S cenou", "5" },
        };
        foreach (var desc in new[] { false, true })
        {
            var (_, text) = Read(await FillRows(200, rows, sortColumn: 2, desc: desc));
            Assert.True(text.IndexOf("S cenou") < text.IndexOf("Bez ceny"));
        }
    }

    [Fact]
    public async Task More_than_a_thousand_rows_is_refused()
    {
        var rows = Enumerable.Range(1, PdfTemplateService.MaxTableRows + 1)
            .Select(i => new List<string> { $"R{i}", $"{i}" })
            .ToList();

        var ex = await Assert.ThrowsAsync<DotNetSigningServer.Exceptions.ApiValidationException>(
            () => FillRows(200, rows));
        Assert.Contains("TABLE_TOO_MANY_ROWS", ex.Message);
    }
}
