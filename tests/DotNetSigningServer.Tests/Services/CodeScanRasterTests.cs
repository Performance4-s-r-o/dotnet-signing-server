using System.Diagnostics;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;
using ImageMagick;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using ZXing;
using ZXing.QrCode;

namespace DotNetSigningServer.Tests.Services;

/// <summary>Skips when Ghostscript is not installed (CI has none; the image and dev machines do).</summary>
public sealed class GhostscriptFactAttribute : FactAttribute
{
    public GhostscriptFactAttribute()
    {
        if (!Available) Skip = "Ghostscript (gs) is not on PATH";
    }

    private static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gs", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// find-codes rasterisation (#71): resolution chosen from the page size before Ghostscript,
/// pages read one at a time, only scanned pages charged, ImageMagick policy in force.
/// </summary>
public class CodeScanRasterTests
{
    private const float A4W = 595.28f, A4H = 841.89f;
    private const float A0W = 2383.94f, A0H = 3370.39f;
    private const float PtPerMetre = 72f / 0.0254f;

    // ── DPI from page size ─────────────────────────────────────────────────

    [Fact]
    public void A4_and_A3_stay_at_300_dpi()
    {
        Assert.Equal(300, CodeScanRaster.DpiFor(A4W, A4H));
        Assert.Equal(300, CodeScanRaster.DpiFor(841.89, 1190.55));
    }

    [Fact]
    public void A0_is_rendered_at_a_lower_dpi_within_the_pixel_budget()
    {
        var dpi = CodeScanRaster.DpiFor(A0W, A0H);
        Assert.NotNull(dpi);
        Assert.InRange(dpi!.Value, 120, 130);
        var px = (A0W / 72.0 * dpi.Value) * (A0H / 72.0 * dpi.Value);
        Assert.True(px <= CodeScanRaster.PixelBudget, $"{px} px");
    }

    [Fact]
    public void Five_by_five_metres_is_too_large()
    {
        Assert.Null(CodeScanRaster.DpiFor(5 * PtPerMetre, 5 * PtPerMetre));
        // About 1.6 m² is where the budget meets 100 DPI.
        Assert.Null(CodeScanRaster.DpiFor(1.4 * PtPerMetre, 1.4 * PtPerMetre));
        Assert.NotNull(CodeScanRaster.DpiFor(1.2 * PtPerMetre, 1.2 * PtPerMetre));
    }

    [Fact]
    public void A_long_thin_page_is_capped_by_the_side_limit()
    {
        // 4.5 m × 10 cm is well under the area budget at 300 DPI, but 53 000 px long.
        var dpi = CodeScanRaster.DpiFor(4.5 * PtPerMetre, 0.1 * PtPerMetre);
        Assert.NotNull(dpi);
        Assert.True(4.5 / 0.0254 * dpi!.Value < CodeScanRaster.MaxSidePx);
    }

    // ── Plan: before any credit or Ghostscript ─────────────────────────────

    [Fact]
    public void Plan_scans_the_first_30_pages_and_charges_only_those()
    {
        var pdf = Pdf(Enumerable.Repeat((A4W, A4H), 35).ToArray());
        var pages = CodeScanRaster.Plan(pdf);

        Assert.Equal(35, pages.TotalPages);
        Assert.Equal(30, pages.PagesProcessed);
        Assert.True(pages.Truncated);
        // ceil(30 / 3) = 10; charging all 35 pages was ceil(35 / 3) = 12.
        Assert.Equal(10, PdfUtilityApiController.FindCodesCredits(pages));
    }

    [Fact]
    public void Plan_charges_a_short_document_as_before()
    {
        var pages = CodeScanRaster.Plan(Pdf((A4W, A4H), (A4W, A4H), (A4W, A4H), (A4W, A4H)));
        Assert.Equal(4, pages.PagesProcessed);
        Assert.False(pages.Truncated);
        Assert.Equal(2, PdfUtilityApiController.FindCodesCredits(pages));
    }

    [Fact]
    public void Plan_refuses_a_page_too_large_before_rasterising()
    {
        var huge = 5 * PtPerMetre;
        var ex = Assert.Throws<ApiValidationException>(() => CodeScanRaster.Plan(Pdf((A4W, A4H), (huge, huge))));
        Assert.Equal("PDF_PAGE_TOO_LARGE", ex.Code);
    }

    [Fact]
    public void Plan_ignores_a_huge_page_that_is_not_scanned()
    {
        var sizes = Enumerable.Repeat((A4W, A4H), 30).Append((5 * PtPerMetre, 5 * PtPerMetre)).ToArray();
        var pages = CodeScanRaster.Plan(Pdf(sizes));
        Assert.Equal(31, pages.TotalPages);
        Assert.Equal(30, pages.PagesProcessed);
    }

    [Fact]
    public void Plan_counts_UserUnit()
    {
        // 1 m × 1 m in points, times UserUnit 3 = 9 m²: refused.
        var pdf = Pdf((PtPerMetre, PtPerMetre));
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            doc.GetPage(1).GetPdfObject().Put(PdfName.UserUnit, new PdfNumber(3));
        }
        var ex = Assert.Throws<ApiValidationException>(() => CodeScanRaster.Plan(output.ToArray()));
        Assert.Equal("PDF_PAGE_TOO_LARGE", ex.Code);
    }

    // ── ImageMagick policy and limits ──────────────────────────────────────

    [Fact]
    public void Policy_refuses_MVG_and_other_coders()
    {
        CodeScanRaster.EnsureConfigured();
        Assert.True(CodeScanRaster.PolicyRefuses(MagickFormat.Mvg));
        Assert.True(CodeScanRaster.PolicyRefuses(MagickFormat.Msl));
        Assert.True(CodeScanRaster.PolicyRefuses(MagickFormat.Svg));
        Assert.True(CodeScanRaster.PolicyRefuses(MagickFormat.Text));
        Assert.True(CodeScanRaster.PolicyRefuses(MagickFormat.Http));
        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage("xc:red", new MagickReadSettings { Width = 2, Height = 2 }));
    }

    /// <summary>
    /// True when reading <paramref name="format"/> never reaches a decoder: refused by the policy, or
    /// no coder for it in this build. Anything else means a coder ran on attacker bytes.
    /// </summary>
    private static bool NeverDecodes(MagickFormat format)
    {
        var data = System.Text.Encoding.ASCII.GetBytes("viewbox 0 0 1 1\n");
        try
        {
            using var image = new MagickImage(data, new MagickReadSettings { Format = format });
            return false;
        }
        catch (MagickPolicyErrorException)
        {
            return true;
        }
        catch (MagickMissingDelegateErrorException)
        {
            return true;
        }
        catch (MagickException)
        {
            return false;
        }
    }

    [Fact]
    public void Only_the_allowlisted_formats_can_be_read()
    {
        CodeScanRaster.EnsureConfigured();
        var decodes = Enum.GetValues<MagickFormat>()
            .Where(f => f != MagickFormat.Unknown && !CodeScanRaster.ReadAllowlist.Contains(f))
            .Where(f => !NeverDecodes(f))
            .ToList();

        // Anything that reached a coder is a way in that nobody decided on.
        Assert.Empty(decodes);
    }

    [Theory]
    [InlineData(MagickFormat.Ps)]
    [InlineData(MagickFormat.Eps)]
    [InlineData(MagickFormat.Ps2)]
    [InlineData(MagickFormat.Ps3)]
    [InlineData(MagickFormat.Epi)]
    [InlineData(MagickFormat.Https)]
    [InlineData(MagickFormat.Ftp)]
    [InlineData(MagickFormat.Label)]
    [InlineData(MagickFormat.Caption)]
    [InlineData(MagickFormat.Inline)]
    [InlineData(MagickFormat.Vid)]
    [InlineData(MagickFormat.Mvg)]
    [InlineData(MagickFormat.Msl)]
    public void Dangerous_coders_never_decode(MagickFormat format)
    {
        CodeScanRaster.EnsureConfigured();
        Assert.True(NeverDecodes(format), format.ToString());
    }

    [Fact]
    public void The_allowlisted_formats_are_not_refused()
    {
        CodeScanRaster.EnsureConfigured();
        foreach (var format in CodeScanRaster.ReadAllowlist)
        {
            Assert.False(CodeScanRaster.PolicyRefuses(format), format.ToString());
        }
    }

    [Theory]
    [InlineData("@{0}")]
    [InlineData("|echo x")]
    [InlineData("url:http://127.0.0.1:9/x.png")]
    [InlineData("https://127.0.0.1:9/x.png")]
    [InlineData("ephemeral:{0}")]
    [InlineData("text:{0}")]
    [InlineData("msl:{0}")]
    [InlineData("ps:{0}")]
    [InlineData("eps:{0}")]
    [InlineData("label:{0}")]
    [InlineData("fd:0")]
    [InlineData("-")]
    [InlineData("URL:http://127.0.0.1:9/x.png")]
    [InlineData("HTTPS://127.0.0.1:9/x.png")]
    [InlineData("PS:{0}")]
    [InlineData("Ephemeral:{0}")]
    public void File_and_network_reads_through_a_path_never_decode(string template)
    {
        CodeScanRaster.EnsureConfigured();
        var file = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "viewbox 0 0 1 1\n");
            var path = template.Replace("{0}", file);
            var ex = Assert.ThrowsAny<MagickException>(() => new MagickImage(path));
            Assert.True(ex is MagickPolicyErrorException or MagickMissingDelegateErrorException, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Only_the_grey_writer_is_allowed()
    {
        CodeScanRaster.EnsureConfigured();
        // A 1 × 1 grey PAM, a readable format without Ghostscript.
        var pam = System.Text.Encoding.ASCII.GetBytes("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nTUPLTYPE GRAYSCALE\nENDHDR\n")
            .Concat(new byte[] { 0x80 }).ToArray();
        using var image = new MagickImage(pam, new MagickReadSettings { Format = MagickFormat.Pam });

        foreach (var format in CodeScanRaster.WriteAllowlist) Assert.NotEmpty(image.ToByteArray(format));
        Assert.Throws<MagickPolicyErrorException>(() => image.ToByteArray(MagickFormat.Png));
        Assert.Throws<MagickPolicyErrorException>(() => image.ToByteArray(MagickFormat.Ps));
        Assert.Throws<MagickPolicyErrorException>(() => image.ToByteArray(MagickFormat.Svg));
    }

    [Fact]
    public void Resource_limits_are_set()
    {
        CodeScanRaster.EnsureConfigured();
        Assert.Equal(20_000ul, (ulong)ResourceLimits.Width);
        Assert.Equal(20_000ul, (ulong)ResourceLimits.Height);
        Assert.Equal(32_000_000ul, (ulong)ResourceLimits.Area);
        Assert.Equal(0ul, (ulong)ResourceLimits.Disk);
        Assert.Equal(60ul, (ulong)ResourceLimits.Time);
        Assert.InRange((ulong)ResourceLimits.Thread, 1ul, 2ul);
        Assert.Equal((ulong)CodeScanRaster.DefaultMemoryBytes(), (ulong)ResourceLimits.Memory);
    }

    // ── Rendering (needs Ghostscript) ──────────────────────────────────────

    [GhostscriptFact]
    public async Task A_code_on_an_A4_page_is_found_where_it_is()
    {
        var pdf = Pdf(new[] { (A4W, A4H) }, page => Qr(page, "P4-71-A4", x: 100, y: 600, module: 4));
        var hits = await Scan(pdf);

        var hit = Assert.Single(hits);
        Assert.Equal("P4-71-A4", hit.GetProperty("value").GetString());
        var box = hit.GetProperty("boundingBox");
        // ZXing reports the finder-pattern centres, 3.5 modules in from the corner.
        Assert.InRange(box.GetProperty("x").GetDouble(), 105, 125);
        Assert.InRange(box.GetProperty("y").GetDouble(), 605, 625);
    }

    [GhostscriptFact]
    public async Task A_code_on_a_CMYK_page_is_found()
    {
        // CMYK content makes ImageMagick route the page through the ps:cmyk delegate and read PAM back.
        var pdf = Pdf(new[] { (A4W, A4H) }, page =>
        {
            Qr(page, "P4-71-CMYK", x: 100, y: 600, module: 4);
            var canvas = new PdfCanvas(page);
            canvas.SetFillColorCmyk(0, 0, 0, 1).Rectangle(10, 10, 5, 5).Fill();
            canvas.Release();
        });

        var hit = Assert.Single(await Scan(pdf));
        Assert.Equal("P4-71-CMYK", hit.GetProperty("value").GetString());
    }

    [GhostscriptFact]
    public async Task A_code_on_an_A0_page_is_found_at_the_reduced_dpi()
    {
        var pdf = Pdf(new[] { (A0W, A0H) }, page => Qr(page, "P4-71-A0", x: 1500, y: 2000, module: 8));
        var pages = CodeScanRaster.Plan(pdf);
        Assert.True(pages.Dpi[0] < 300);

        var hit = Assert.Single(await Scan(pdf));
        Assert.Equal("P4-71-A0", hit.GetProperty("value").GetString());
        // Coordinates are converted with the page's own DPI, not 300.
        var box = hit.GetProperty("boundingBox");
        Assert.InRange(box.GetProperty("x").GetDouble(), 1515, 1545);
        Assert.InRange(box.GetProperty("y").GetDouble(), 2015, 2045);
    }

    [GhostscriptFact]
    public async Task Thirty_A4_pages_keep_ImageMagick_process_memory_under_500_MB()
    {
        var pdf = Pdf(Enumerable.Repeat((A4W, A4H), 30).ToArray(), page => Qr(page, "P4-71-MEM", 100, 600, 4));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(10);
            }
        });

        var hits = await Scan(pdf);
        stop.Cancel();
        await sampler;

        Assert.Equal(30, hits.Count);
        var grownMb = (peak - baseline) / (1024.0 * 1024.0);
        Assert.True(grownMb < 500, $"Process grew by {grownMb:F0} MB while scanning 30 A4 pages");
    }

    // ── What reaches Ghostscript ───────────────────────────────────────────

    private const string PostScript = "%!PS\n/Helvetica findfont 20 scalefont setfont 10 10 moveto (X) show showpage quit\n";

    private static byte[] Polyglot() =>
        System.Text.Encoding.ASCII.GetBytes(PostScript).Concat(Pdf((A4W, A4H))).ToArray();

    [Fact]
    public void Only_bytes_starting_with_the_pdf_header_pass()
    {
        Assert.True(CodeScanRaster.StartsWithPdfHeader(Pdf((A4W, A4H))));
        Assert.False(CodeScanRaster.StartsWithPdfHeader(System.Text.Encoding.ASCII.GetBytes(PostScript)));
        Assert.False(CodeScanRaster.StartsWithPdfHeader(Polyglot()));
        Assert.False(CodeScanRaster.StartsWithPdfHeader(Array.Empty<byte>()));
    }

    [Fact]
    public void Plan_rewrites_a_polyglot_into_a_clean_pdf()
    {
        var polyglot = Polyglot();
        var pages = CodeScanRaster.Plan(polyglot);

        Assert.NotNull(pages.CleanPdf);
        Assert.True(CodeScanRaster.StartsWithPdfHeader(pages.CleanPdf!));
        Assert.DoesNotContain("%!PS", System.Text.Encoding.Latin1.GetString(pages.CleanPdf!));
    }

    [GhostscriptFact]
    public async Task PostScript_bytes_sent_as_pdf_are_refused_before_Ghostscript()
    {
        var ps = System.Text.Encoding.ASCII.GetBytes(PostScript);
        var ex = await Assert.ThrowsAsync<ApiValidationException>(() =>
            CodeScanRaster.WithPageAsync(ps, 0, 100, p => p.Width, CancellationToken.None));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [GhostscriptFact]
    public async Task A_polyglot_is_refused_when_it_reaches_the_rasteriser_raw()
    {
        var ex = await Assert.ThrowsAsync<ApiValidationException>(() =>
            CodeScanRaster.WithPageAsync(Polyglot(), 0, 100, p => p.Width, CancellationToken.None));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [GhostscriptFact]
    public async Task A_polyglot_scanned_through_the_normal_path_renders_only_the_pdf()
    {
        var polyglot = Polyglot();
        var pages = CodeScanRaster.Plan(polyglot);
        var width = await CodeScanRaster.WithPageAsync(pages.CleanPdf!, 0, pages.Dpi[0], p => (int)p.Width, CancellationToken.None);
        // A4 at 100+ DPI; the PostScript page (default media) is not what was rendered.
        Assert.InRange(width, 800, 2600);
    }

    [Fact]
    public void Plan_maps_a_broken_page_tree_to_PDF_INVALID()
    {
        var ex = Assert.Throws<ApiValidationException>(
            () => CodeScanRaster.Plan(System.Text.Encoding.ASCII.GetBytes(PdfSafetyGuardTests.PageTreeCycle)));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void Plan_maps_garbage_after_the_header_to_PDF_INVALID()
    {
        var ex = Assert.Throws<ApiValidationException>(() => CodeScanRaster.Plan("%PDF-1.4\nnot a pdf"u8.ToArray()));
        Assert.Equal("PDF_INVALID", ex.Code);
    }

    [Fact]
    public void Plan_maps_a_password_protected_pdf_to_PDF_ENCRYPTED()
    {
        using var ms = new MemoryStream();
        var props = new WriterProperties().SetStandardEncryption(
            "user"u8.ToArray(), "owner"u8.ToArray(), iText.Kernel.Pdf.EncryptionConstants.ALLOW_PRINTING,
            iText.Kernel.Pdf.EncryptionConstants.ENCRYPTION_AES_128);
        using (var doc = new PdfDocument(new PdfWriter(ms, props))) doc.AddNewPage();
        var ex = Assert.Throws<ApiValidationException>(() => CodeScanRaster.Plan(ms.ToArray()));
        Assert.Equal("PDF_ENCRYPTED", ex.Code);
    }

    [Fact]
    public void The_Ghostscript_delegate_never_passes_dDOPS()
    {
        var delegates = ImageMagick.Configuration.ConfigurationFiles.Default.Delegates.Data;
        Assert.DoesNotContain("dDOPS", delegates, StringComparison.OrdinalIgnoreCase);
    }

    [GhostscriptFact]
    public async Task A_PostScript_XObject_in_a_pdf_is_not_executed()
    {
        // The XObject would paint the whole page black if Ghostscript ran it (needs -dDOPS).
        var pdf = Pdf(new[] { (A4W, A4H) }, page =>
        {
            var ps = new PdfStream(System.Text.Encoding.ASCII.GetBytes("0 setgray 0 0 600 850 rectfill"));
            ps.Put(PdfName.Type, PdfName.XObject);
            ps.Put(PdfName.Subtype, new PdfName("PS"));
            var xobjects = new PdfDictionary();
            xobjects.Put(new PdfName("P0"), ps);
            page.GetResources().GetPdfObject().Put(PdfName.XObject, xobjects);
            new PdfCanvas(page).GetContentStream().GetOutputStream().WriteBytes("/P0 Do\n"u8.ToArray());
        });
        var pages = CodeScanRaster.Plan(pdf);
        var mean = await CodeScanRaster.WithPageAsync(pages.CleanPdf, 0, 72, p =>
            p.ToByteArray(MagickFormat.Gray).Select(b => (int)b).Average(), CancellationToken.None);
        Assert.True(mean > 200, $"page mean {mean}: the PostScript was executed");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<List<JsonElement>> Scan(byte[] pdf)
    {
        var pages = CodeScanRaster.Plan(pdf);
        var results = await PdfUtilityApiController.ScanCodesAsync(
            pdf, pages, PdfUtilityApiController.ParseFormats("qr"), CancellationToken.None);
        return JsonSerializer.SerializeToElement(results, JsonSerializerOptions.Web).EnumerateArray().ToList();
    }

    private static byte[] Pdf(params (float W, float H)[] sizes) => Pdf(sizes, null);

    private static byte[] Pdf((float W, float H)[] sizes, Action<PdfPage>? draw)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            foreach (var (w, h) in sizes)
            {
                var page = doc.AddNewPage(new PageSize(w, h));
                draw?.Invoke(page);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A QR code drawn as vector modules, bottom-left corner at (x, y) in points.</summary>
    private static void Qr(PdfPage page, string text, float x, float y, float module)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 });
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
}
