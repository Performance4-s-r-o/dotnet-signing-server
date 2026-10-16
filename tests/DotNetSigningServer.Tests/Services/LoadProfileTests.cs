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

    // ── #74: base64 v JSONu vs. binární kanál ─────────────────────────────

    private sealed record MemoryRow(string Name, double Seconds, double AllocatedMb, double PeakHeapMb, double PeakProcessMb);

    /// <summary>
    /// Paměť jedné operace: kolik se alokovalo, špička spravované haldy (vzorkovaná po 2 ms, včetně
    /// smetí, které GC ještě nesebral — to proces opravdu drží) a špička pracovní sady procesu.
    /// </summary>
    private static async Task<MemoryRow> MeasureMemory(string name, Func<Task> work)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseWs = process.WorkingSet64;
        var baseHeap = GC.GetTotalMemory(false);
        var baseAlloc = GC.GetTotalAllocatedBytes(precise: true);
        long peakHeap = baseHeap, peakWs = baseWs;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                process.Refresh();
                peakWs = Math.Max(peakWs, process.WorkingSet64);
                try { await Task.Delay(2, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        try
        {
            await work();
        }
        finally
        {
            sw.Stop();
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
            stop.Cancel();
            await sampler;
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - baseAlloc;
        return new MemoryRow(name, sw.Elapsed.TotalSeconds, allocated / 1048576.0,
            (peakHeap - baseHeap) / 1048576.0, (peakWs - baseWs) / 1048576.0);
    }

    /// <summary>
    /// Presign nad ~20 MB PDF třemi cestami: JSON jak byl na mainu (SafetyGuard i presign dekódují
    /// base64 každý zvlášť), JSON po #74 (dekóduje se jednou) a binární kanál. Tělo požadavku je
    /// připravené předem — v provozu ho drží portál, ne my; server čte z proudu jako zde.
    /// </summary>
    [Fact]
    public async Task BinaryChannelProfile()
    {
        if (!Enabled) return;
        var limits = new LimitsOptions();
        var guard = new PdfSafetyGuard(TestHelpers.WrapOptions(limits));
        var limitGuard = new ContentLimitGuard(TestHelpers.WrapOptions(limits));
        var signing = new PdfSigningService();
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var pem = TestHelpers.CreateTestCertificate().CertificatePem;

        // Sken: náhodná data v obrázkových streamech (nekomprimují se), těsně pod limitem 20 MB.
        var rng = new Random(74);
        var pdf = Pdf(19, 595, 842, (_, p, _) =>
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
        Assert.True(pdf.LongLength <= limits.PdfMaxBytes, $"{pdf.LongLength} B");

        var metadata = new Models.PreSignInput
        {
            CertificatePem = pem,
            Reason = "profil",
            SignRect = new Models.SignRect { X = 50, Y = 50, Width = 200, Height = 60 },
        };
        var withPdf = new Models.PreSignInput { CertificatePem = pem, Reason = "profil", SignRect = metadata.SignRect, PdfContent = Convert.ToBase64String(pdf) };
        var jsonBody = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(withPdf, json);
        withPdf = null;

        byte[] multipartBody;
        const string boundary = "p4-load-profile-boundary";
        using (var content = new MultipartFormDataContent(boundary))
        {
            content.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(metadata, json), Encoding.UTF8, "application/json"), BinaryPdfChannel.MetadataPart);
            var part = new ByteArrayContent(pdf);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");
            multipartBody = await content.ReadAsByteArrayAsync();
        }
        var contentType = $"multipart/form-data; boundary={boundary}";
        var written = new List<string>();

        async Task JsonMain()
        {
            var input = await System.Text.Json.JsonSerializer.DeserializeAsync<Models.PreSignInput>(new MemoryStream(jsonBody), json);
            limitGuard.EnsurePdfWithinLimit(input!.PdfContent, "p");
            guard.EnsureSafePdf(input.PdfContent, "p");
            written.Add(signing.HandlePreSign(input, "Signature_profile").PresignedPdfPath);
        }

        async Task JsonOnce()
        {
            var input = await System.Text.Json.JsonSerializer.DeserializeAsync<Models.PreSignInput>(new MemoryStream(jsonBody), json);
            limitGuard.EnsurePdfWithinLimit(input!.PdfContent, "p");
            var bytes = Convert.FromBase64String(input.PdfContent);
            guard.EnsureSafePdf(bytes, "p");
            input.PdfContent = string.Empty;
            written.Add(signing.HandlePreSign(input, bytes, "Signature_profile").PresignedPdfPath);
        }

        async Task Binary()
        {
            var (input, bytes) = await BinaryPdfChannel.ReadAsync<Models.PreSignInput>(
                contentType, new MemoryStream(multipartBody), multipartBody.LongLength, json, limits.PdfMaxBytes, 8 * 1024 * 1024, CancellationToken.None);
            guard.EnsureSafePdf(bytes, "p");
            written.Add(signing.HandlePreSign(input, bytes, "Signature_profile").PresignedPdfPath);
        }

        // Jen čtení vstupu, bez práce: kolik stojí samotný kanál.
        async Task JsonParseOnly()
        {
            var input = await System.Text.Json.JsonSerializer.DeserializeAsync<Models.PreSignInput>(new MemoryStream(jsonBody), json);
            GC.KeepAlive(Convert.FromBase64String(input!.PdfContent));
        }

        async Task BinaryParseOnly()
        {
            var (_, bytes) = await BinaryPdfChannel.ReadAsync<Models.PreSignInput>(
                contentType, new MemoryStream(multipartBody), multipartBody.LongLength, json, limits.PdfMaxBytes, 8 * 1024 * 1024, CancellationToken.None);
            GC.KeepAlive(bytes);
        }

        var scenarios = new (string Name, Func<Task> Work)[]
        {
            ("čtení vstupu: JSON base64 → bajty", JsonParseOnly),
            ("čtení vstupu: multipart → bajty", BinaryParseOnly),
            ("presign: JSON jako na mainu (2× dekódování)", JsonMain),
            ("presign: JSON po #74 (1× dekódování)", JsonOnce),
            ("presign: binární kanál", Binary),
            ("presign 4× souběžně: JSON jako na mainu", () => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(JsonMain)))),
            ("presign 4× souběžně: JSON po #74", () => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(JsonOnce)))),
            ("presign 4× souběžně: binární kanál", () => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Binary)))),
        };
        var rows = new List<MemoryRow>();
        foreach (var (name, work) in scenarios)
        {
            await work(); // zahřátí (JIT, iText)
            var samples = new List<MemoryRow>();
            for (var i = 0; i < 5; i++) samples.Add(await MeasureMemory(name, work));
            // Medián z pěti podle špičky haldy: špička závisí na tom, kdy přesně GC proběhne.
            rows.Add(samples.OrderBy(s => s.PeakHeapMb).ElementAt(2));
        }
        foreach (var file in written)
        {
            try { File.Delete(file); } catch (IOException) { }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Stroj: {Environment.ProcessorCount} jader, .NET {Environment.Version}, {DateTime.UtcNow:u}");
        sb.AppendLine($"PDF {pdf.Length / 1048576.0:0.0} MB, JSON tělo {jsonBody.Length / 1048576.0:0.0} MB, multipart tělo {multipartBody.Length / 1048576.0:0.0} MB");
        sb.AppendLine();
        sb.AppendLine("| scénář | čas [s] | alokováno [MB] | špička haldy [MB] | špička procesu [MB] |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows) sb.AppendLine($"| {r.Name} | {r.Seconds:0.00} | {r.AllocatedMb:0} | {r.PeakHeapMb:0} | {r.PeakProcessMb:0} |");
        sb.AppendLine();
        sb.AppendLine("Pozn.: medián z pěti běhů po zahřátí. Špička haldy zahrnuje smetí, které GC ještě nesebral.");
        var path = Environment.GetEnvironmentVariable("P4_LOAD_BINARY_OUT")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p4-load-binary-channel.md");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Timestamp a pečeť nad ~18 MB PDF: JSON (base64 → dvě dekódování, jako dnes) proti multipartu
    /// (bajty rovnou). Jen s <c>P4_LOAD_PROFILE=1</c>; výsledek jde do <c>P4_LOAD_BINARY2_OUT</c>.
    /// Jediný assert: multipart alokuje méně než JSON (čísla závisejí na stroji, práh není).
    /// </summary>
    [Fact]
    public async Task BinaryChannelProfile_TimestampAndSeal()
    {
        if (!Enabled) return;
        var limits = new LimitsOptions();
        var guard = new PdfSafetyGuard(TestHelpers.WrapOptions(limits));
        var limitGuard = new ContentLimitGuard(TestHelpers.WrapOptions(limits));
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var (_, pfx, password) = TestHelpers.CreateTestCertificate();
        var sealOptions = new SealOptions { Enabled = true, PfxBase64 = pfx, PfxPassword = password, Reason = "profil", Location = "l" };
        var signing = new PdfSigningService(TestHelpers.WrapOptions(sealOptions), null, new PdfVisualSigningService());
        var sealing = new PdfSealingService(TestHelpers.WrapOptions(sealOptions), signing, new PdfVisualSigningService());
        var rect = new Models.SignRect { X = 50, Y = 50, Width = 200, Height = 60 };

        var rng = new Random(74);
        var pdf = Pdf(19, 595, 842, (_, p, _) =>
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
        Assert.True(pdf.LongLength <= limits.PdfMaxBytes);
        var base64 = Convert.ToBase64String(pdf);

        byte[] Body(object meta) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(meta, meta.GetType(), json);
        byte[] Multipart(object meta, string boundary)
        {
            using var content = new MultipartFormDataContent(boundary);
            content.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(meta, meta.GetType(), json), Encoding.UTF8, "application/json"), BinaryPdfChannel.MetadataPart);
            var part = new ByteArrayContent(pdf);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");
            return content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        const string boundary = "p4-load-profile-boundary";
        var contentType = $"multipart/form-data; boundary={boundary}";
        var tsa = new LocalTsaClient();
        var tsJson = Body(new Models.DocumentTimestampInput { PdfContent = base64, SignRect = rect, Visible = false });
        var tsMulti = Multipart(new Models.DocumentTimestampInput { SignRect = rect, Visible = false }, boundary);
        var sealJson = Body(new Models.SealInput { PdfContent = base64, SignRect = rect, Reason = "profil", Location = "l" });
        var sealMulti = Multipart(new Models.SealInput { SignRect = rect, Reason = "profil", Location = "l" }, boundary);
        base64 = null!;

        async Task TsJson()
        {
            var input = await System.Text.Json.JsonSerializer.DeserializeAsync<Models.DocumentTimestampInput>(new MemoryStream(tsJson), json);
            limitGuard.EnsurePdfWithinLimit(input!.PdfContent, "p");
            guard.EnsureSafePdf(input.PdfContent, "p");
            GC.KeepAlive(signing.ApplyDocumentTimestamp(input, tsa));
        }
        async Task TsBinary()
        {
            var (input, bytes) = await BinaryPdfChannel.ReadAsync<Models.DocumentTimestampInput>(
                contentType, new MemoryStream(tsMulti), tsMulti.LongLength, json, limits.PdfMaxBytes, 8 * 1024 * 1024, CancellationToken.None);
            guard.EnsureSafePdf(bytes, "p");
            GC.KeepAlive(signing.ApplyDocumentTimestamp(input, tsa, bytes));
        }
        async Task SealJson()
        {
            var input = await System.Text.Json.JsonSerializer.DeserializeAsync<Models.SealInput>(new MemoryStream(sealJson), json);
            limitGuard.EnsurePdfWithinLimit(input!.PdfContent, "p");
            guard.EnsureSafePdf(input.PdfContent, "p");
            GC.KeepAlive(sealing.ApplySeal(input));
        }
        async Task SealBinary()
        {
            var (input, bytes) = await BinaryPdfChannel.ReadAsync<Models.SealInput>(
                contentType, new MemoryStream(sealMulti), sealMulti.LongLength, json, limits.PdfMaxBytes, 8 * 1024 * 1024, CancellationToken.None);
            guard.EnsureSafePdf(bytes, "p");
            GC.KeepAlive(sealing.ApplySeal(input, bytes));
        }

        var scenarios = new (string Name, Func<Task> Work)[]
        {
            ("timestamp: JSON base64", TsJson),
            ("timestamp: multipart", TsBinary),
            ("seal: JSON base64", SealJson),
            ("seal: multipart", SealBinary),
        };
        var rows = new List<MemoryRow>();
        foreach (var (name, work) in scenarios)
        {
            await work();
            var samples = new List<MemoryRow>();
            for (var i = 0; i < 3; i++) samples.Add(await MeasureMemory(name, work));
            rows.Add(samples.OrderBy(x => x.AllocatedMb).ElementAt(1));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Stroj: {Environment.ProcessorCount} jader, .NET {Environment.Version}, {DateTime.UtcNow:u}");
        sb.AppendLine($"PDF {pdf.Length / 1048576.0:0.0} MB");
        sb.AppendLine();
        sb.AppendLine("| scénář | čas [s] | alokováno [MB] | špička haldy [MB] | špička procesu [MB] |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows) sb.AppendLine($"| {r.Name} | {r.Seconds:0.00} | {r.AllocatedMb:0} | {r.PeakHeapMb:0} | {r.PeakProcessMb:0} |");
        var path = Environment.GetEnvironmentVariable("P4_LOAD_BINARY2_OUT")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p4-load-binary-channel-2.md");
        File.WriteAllText(path, sb.ToString());

        Assert.True(rows[1].AllocatedMb < rows[0].AllocatedMb, "timestamp: multipart should allocate less than JSON");
        Assert.True(rows[3].AllocatedMb < rows[2].AllocatedMb, "seal: multipart should allocate less than JSON");
    }
}
