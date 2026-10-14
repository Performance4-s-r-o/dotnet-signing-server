using System.Diagnostics;
using System.Text;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Forms;
using iText.Forms.Fields;
using ZXing;
using ZXing.QrCode;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Měření pro plán 2.6 (#223): kolik stojí vstupní kontrola a find-codes na reálných a nepříznivých
/// PDF. NENÍ součástí běžné sady: spouští se jen s <c>P4_LOAD_PROFILE=1</c> a výsledek zapíše do
/// souboru z <c>P4_LOAD_OUT</c>. Čísla závisejí na stroji; slouží k odhadu limitů, ne jako práh.
/// </summary>
public class LoadProfileTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("P4_LOAD_PROFILE") == "1";

    private sealed record Row(string Name, double Seconds, double PeakMb, string Note);

    private static readonly List<Row> Rows = new();

    private static async Task<T> Measure<T>(string name, Func<Task<T>> work, string note = "")
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        var baseline = process.WorkingSet64;
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                try { await Task.Delay(15, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        T result;
        try
        {
            result = await work();
        }
        finally
        {
            sw.Stop();
            stop.Cancel();
            await sampler;
        }
        process.Refresh();
        peak = Math.Max(peak, process.WorkingSet64);
        Rows.Add(new Row(name, sw.Elapsed.TotalSeconds, (peak - baseline) / 1048576.0, note));
        return result;
    }

    private static PdfSafetyGuard Guard() => new(TestHelpers.WrapOptions(new LimitsOptions()));

    private static byte[] Pdf(int pages, float w, float h, Action<PdfDocument, PdfPage, int>? draw = null)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            for (var i = 1; i <= pages; i++)
            {
                var page = doc.AddNewPage(new PageSize(w, h));
                draw?.Invoke(doc, page, i);
            }
        }
        return ms.ToArray();
    }

    private static void Qr(PdfPage page, string text, float x, float y, float module)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0 });
        var canvas = new PdfCanvas(page);
        canvas.SetFillColorRgb(0, 0, 0);
        for (var row = 0; row < matrix.Height; row++)
            for (var col = 0; col < matrix.Width; col++)
            {
                if (!matrix[col, row]) continue;
                canvas.Rectangle(x + col * module, y + (matrix.Height - 1 - row) * module, module, module);
            }
        canvas.Fill();
        canvas.Release();
    }

    [Fact]
    public async Task Profile()
    {
        if (!Enabled) return;
        var guard = Guard();

        // ── vstupní kontrola ──────────────────────────────────────────────
        var tiny = Pdf(1, 595, 842);
        await Measure("guard: 1 strana A4", () => { guard.EnsureSafePdf(tiny, "p"); return Task.FromResult(0); });

        var text500 = Pdf(500, 595, 842, (_, p, i) => Qr(p, $"P{i}", 50, 50, 3));
        await Measure($"guard: 500 stran s QR ({text500.Length / 1048576.0:0.0} MB)",
            () => { guard.EnsureSafePdf(text500, "p"); return Task.FromResult(0); });

        // Sken: ~20 MB náhodných dat v obrázkových streamech (nedekomprimuje se).
        var rng = new Random(11);
        var heavy = Pdf(20, 595, 842, (_, p, _) =>
        {
            var data = new byte[1_000_000];
            rng.NextBytes(data);
            var image = new PdfStream(data);
            image.Put(PdfName.Type, PdfName.XObject);
            image.Put(PdfName.Subtype, PdfName.Image);
            image.Put(PdfName.Width, new PdfNumber(1000));
            image.Put(PdfName.Height, new PdfNumber(1000));
            image.Put(PdfName.ColorSpace, PdfName.DeviceGray);
            image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            p.GetPdfObject().Put(new PdfName("Scan"), image);
        });
        await Measure($"guard: sken 20 stran ({heavy.Length / 1048576.0:0.0} MB)",
            () => { guard.EnsureSafePdf(heavy, "p"); return Task.FromResult(0); });

        var fields = Pdf(1, 595, 842, (doc, _, _) =>
        {
            var form = PdfFormCreator.GetAcroForm(doc, true);
            for (var i = 0; i < 4500; i++)
            {
                form.AddField(new TextFormFieldBuilder(doc, $"f{i}")
                    .SetWidgetRectangle(new Rectangle(10, 10 + (i % 700), 40, 8)).CreateText());
            }
        });
        await Measure($"guard: 4 500 polí formuláře ({fields.Length / 1048576.0:0.0} MB)",
            () => { guard.EnsureSafePdf(fields, "p"); return Task.FromResult(0); });

        var bomb = Pdf(1, 595, 842, (_, p, _) =>
        {
            var s = new PdfStream(new byte[60 * 1024 * 1024]);
            s.SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);
            p.GetPdfObject().Put(new PdfName("Bomb"), s);
        });
        await Measure($"guard: bomba 60 MB → {bomb.Length / 1024} KB (pod limitem 64 MB)",
            () => { guard.EnsureSafePdf(bomb, "p"); return Task.FromResult(0); });

        // ── find-codes ────────────────────────────────────────────────────
        CodeScanRaster.EnsureConfigured();
        var a4x30 = Pdf(30, 595, 842, (_, p, i) => Qr(p, $"P4-{i}", 100, 600, 4));
        var a4x30result = await Measure("find-codes: 30× A4 (1 požadavek)", async () =>
        {
            var plan = CodeScanRaster.Plan(a4x30);
            return await PdfUtilityApiController.ScanCodesAsync(a4x30, plan, PdfUtilityApiController.ParseFormats("qr"), CancellationToken.None);
        }, "sériově po stránkách");

        var a4x10 = Pdf(10, 595, 842, (_, p, i) => Qr(p, $"P4-{i}", 100, 600, 4));
        foreach (var parallel in new[] { 2, 4, 8 })
        {
            await Measure($"find-codes: {parallel}× souběžně 10× A4", async () =>
            {
                // Task.Run: každý požadavek na vlastním vlákně jako v ASP.NET. Bez něj by se
                // synchronní renderování prvního spustilo celé v jeho vlákně dřív než druhé.
                await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
                {
                    var plan = CodeScanRaster.Plan(a4x10);
                    return await PdfUtilityApiController.ScanCodesAsync(a4x10, plan, PdfUtilityApiController.ParseFormats("qr"), CancellationToken.None);
                })));
                return 0;
            }, $"pool R = {CodeScanRaster.PoolSize}");
        }

        var a0 = Pdf(3, 2384, 3370, (_, p, i) => Qr(p, $"A0-{i}", 1500, 2000, 8));
        await Measure("find-codes: 3× A0 (snížené DPI)", async () =>
        {
            var plan = CodeScanRaster.Plan(a0);
            return await PdfUtilityApiController.ScanCodesAsync(a0, plan, PdfUtilityApiController.ParseFormats("qr"), CancellationToken.None);
        });

        // ── zápis ─────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine($"Stroj: {Environment.ProcessorCount} jader, .NET {Environment.Version}, {DateTime.UtcNow:u}");
        sb.AppendLine();
        sb.AppendLine("| scénář | čas [s] | špička procesu [MB] | poznámka |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in Rows) sb.AppendLine($"| {r.Name} | {r.Seconds:0.00} | {r.PeakMb:0} | {r.Note} |");
        sb.AppendLine();
        sb.AppendLine($"Pozn.: špička je jen proces testu; Ghostscript běží jako potomek a v ní není.");
        var path = Environment.GetEnvironmentVariable("P4_LOAD_OUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p4-load-profile.md");
        File.WriteAllText(path, sb.ToString());
        _ = a4x30result;
    }
}
