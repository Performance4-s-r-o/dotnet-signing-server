using DotNetSigningServer.Exceptions;
using ImageMagick;
using ImageMagick.Configuration;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services;

/// <summary>
/// Rasterisation for <c>/api/find-codes</c> (#71): which pages, at what resolution, under
/// which limits, and how many at once.
///
/// It used to read the first 30 pages at 300 DPI in one call and keep them all, with no
/// ImageMagick limits and no security policy: about 2 GB for 30 A4 pages, 5 GB for one
/// 2 × 2 m page, and Ghostscript free to use any coder ImageMagick knows on untrusted input.
/// Now a page is read, decoded and released before the next one; its resolution is chosen
/// from its size before Ghostscript ever sees it; and the process-wide limits and policy
/// turn anything that still gets past that into an exception rather than a dead process.
/// </summary>
public static class CodeScanRaster
{
    /// <summary>Pages scanned, from the first. Pages after these are neither rendered nor charged.</summary>
    public const int MaxPages = 30;

    /// <summary>Resolution a page is rendered at when it fits the budget (A4, A3).</summary>
    public const double TargetDpi = 300;

    /// <summary>
    /// Below this a code of ordinary size no longer has enough pixels per module to decode,
    /// so the page is refused rather than scanned for nothing (and charged).
    /// </summary>
    public const double MinDpi = 100;

    /// <summary>
    /// Pixels of one rendered page. 25 Mpx keeps A3 (17.4 Mpx) at 300 DPI and puts A0 at
    /// about 127 DPI; at <see cref="MinDpi"/> it is about 1.6 m² of paper.
    /// </summary>
    public const double PixelBudget = 25_000_000;

    /// <summary>ImageMagick width/height limit; also caps the DPI of long thin pages.</summary>
    public const uint MaxSidePx = 20_000;

    /// <summary>ImageMagick area limit: the budget plus headroom for rounding in Ghostscript.</summary>
    public const long AreaLimit = 32_000_000;

    /// <summary>Seconds one image may live (ImageMagick's per-image TTL, inherited by clones).</summary>
    public const ulong TimeLimitSeconds = 60;

    /// <summary>
    /// Rasterisations running at once in the process, R = max(1, ⌊cores / 2⌋). A page holds a
    /// slot from Ghostscript to the last decode, so memory is bounded by R pages, not by the
    /// number of requests — the per-user and global request semaphores (#69, #93) let more
    /// requests in than this, and that is fine: they wait here, inside their own deadline.
    /// </summary>
    public static int PoolSize { get; } = Math.Max(1, Environment.ProcessorCount / 2);

    private static readonly SemaphoreSlim Pool = new(PoolSize, PoolSize);
    private static readonly object ConfigureLock = new();
    private static bool _policyApplied;

    /// <summary>
    /// What ImageMagick may do in this process. Only reading PDF (which hands the page to
    /// Ghostscript and reads back its PNG) and writing raw grey bytes for the decoder.
    /// Everything else is refused, including the coders that have been the way in before:
    /// MVG, MSL, EPHEMERAL, URL/HTTP, SVG, TEXT, and "@file" / "|command" paths.
    /// The Ghostscript delegates carry -dSAFER already (Magick.NET's delegates.xml), and
    /// Ghostscript 9.50+ is SAFER by default; only the "gs" delegate may run.
    /// </summary>
    internal const string Policy = """
        <policymap>
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read" pattern="{PDF,PNG,PAM}" />
          <policy domain="coder" rights="write" pattern="GRAY" />
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="delegate" rights="execute" pattern="gs" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="path" rights="none" pattern="|*" />
        </policymap>
        """;

    /// <summary>
    /// Policy and limits, once per process. Must run before ImageMagick is used for the first
    /// time — a policy given later is silently ignored — which is why Program.cs calls it at
    /// startup and every rasterisation calls <see cref="EnsureConfigured"/>. Throws when the
    /// policy did not take, so the server does not start without it.
    /// </summary>
    /// <param name="memoryBytes">Pixel-cache memory; 0 = 1.5 GB below 8 cores, 2.5 GB from 8.</param>
    public static void Configure(long memoryBytes)
    {
        lock (ConfigureLock)
        {
            if (!_policyApplied)
            {
                var files = ConfigurationFiles.Default;
                files.Policy.Data = Policy;
                MagickNET.Initialize(files);
                if (!PolicyRefuses(MagickFormat.Mvg))
                {
                    throw new InvalidOperationException(
                        "ImageMagick security policy is not in effect (ImageMagick was used before CodeScanRaster.Configure).");
                }
                _policyApplied = true;
            }

            ResourceLimits.Width = MaxSidePx;
            ResourceLimits.Height = MaxSidePx;
            ResourceLimits.Area = (ulong)AreaLimit;
            ResourceLimits.Memory = (ulong)(memoryBytes > 0 ? memoryBytes : DefaultMemoryBytes());
            // Nothing spills to disk: what does not fit in memory fails.
            ResourceLimits.Disk = 0;
            ResourceLimits.Time = TimeLimitSeconds;
            ResourceLimits.Thread = Environment.ProcessorCount >= 8 ? 2u : 1u;
        }
    }

    /// <summary>Configures with defaults when startup did not (tests, tools).</summary>
    public static void EnsureConfigured()
    {
        if (_policyApplied) return;
        Configure(0);
    }

    internal static long DefaultMemoryBytes() =>
        Environment.ProcessorCount >= 8 ? 2_500L * 1024 * 1024 : 1_536L * 1024 * 1024;

    /// <summary>True when reading <paramref name="format"/> is refused by the policy.</summary>
    internal static bool PolicyRefuses(MagickFormat format)
    {
        try
        {
            var data = System.Text.Encoding.ASCII.GetBytes("viewbox 0 0 1 1\n");
            using var image = new MagickImage(data, new MagickReadSettings { Format = format });
            return false;
        }
        catch (MagickPolicyErrorException)
        {
            return true;
        }
        catch (MagickException)
        {
            // Refused for another reason: the coder ran, so the policy did not stop it.
            return false;
        }
    }

    /// <summary>The scan of one document: page count, and the DPI of each page that is rendered.</summary>
    public sealed record ScanPages(int TotalPages, IReadOnlyList<double> Dpi)
    {
        public int PagesProcessed => Dpi.Count;
        public bool Truncated => TotalPages > PagesProcessed;
    }

    /// <summary>
    /// Opens the PDF without rendering it and decides each scanned page's resolution.
    /// Throws <c>PDF_PAGE_TOO_LARGE</c> when one of the scanned pages would fall under
    /// <see cref="MinDpi"/> — before any credit is reserved and before Ghostscript runs.
    /// </summary>
    public static ScanPages Plan(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        var total = doc.GetNumberOfPages();
        var dpi = new List<double>(Math.Min(total, MaxPages));
        for (var i = 1; i <= Math.Min(total, MaxPages); i++)
        {
            var page = doc.GetPage(i);
            // MediaBox: what Ghostscript renders unless told to use the CropBox.
            var box = page.GetMediaBox();
            var unit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1.0;
            if (unit <= 0) unit = 1.0;
            var pageDpi = DpiFor(box.GetWidth() * unit, box.GetHeight() * unit)
                ?? throw new ApiValidationException("PDF_PAGE_TOO_LARGE", $"Page {i} is too large to rasterise");
            dpi.Add(pageDpi);
        }
        return new ScanPages(total, dpi);
    }

    /// <summary>
    /// Resolution for a page of the given size in points: 300 DPI, lowered so the page fits
    /// <see cref="PixelBudget"/> and neither side exceeds <see cref="MaxSidePx"/>; null when
    /// that would be under <see cref="MinDpi"/>.
    /// </summary>
    public static double? DpiFor(double widthPt, double heightPt)
    {
        var w = Math.Abs(widthPt) / 72.0;
        var h = Math.Abs(heightPt) / 72.0;
        if (w <= 0 || h <= 0) return TargetDpi;

        var budgetDpi = Math.Sqrt(PixelBudget / (w * h));
        // One pixel of headroom: Ghostscript rounds the raster size up.
        var sideDpi = (MaxSidePx - 1) / Math.Max(w, h);
        var dpi = Math.Floor(Math.Min(TargetDpi, Math.Min(budgetDpi, sideDpi)));
        return dpi < MinDpi ? null : dpi;
    }

    /// <summary>
    /// Renders one page (zero-based) as 8-bit grey, holding a pool slot until
    /// <paramref name="use"/> returns. The image is disposed afterwards.
    /// </summary>
    public static async Task<T> WithPageAsync<T>(
        byte[] pdf, int pageIndex, double dpi, Func<MagickImage, T> use, CancellationToken ct)
    {
        EnsureConfigured();
        await Pool.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var settings = new MagickReadSettings
            {
                Density = new Density(dpi, dpi),
                Format = MagickFormat.Pdf,
                FrameIndex = (uint)pageIndex,
                FrameCount = 1,
            };
            using var image = new MagickImage(pdf, settings);
            image.ColorSpace = ColorSpace.sRGB;
            image.Alpha(AlphaAction.Remove);
            image.ColorType = ColorType.Grayscale;
            image.Depth = 8;
            return use(image);
        }
        finally
        {
            Pool.Release();
        }
    }
}
