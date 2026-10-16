namespace DotNetSigningServer.Options;

public class LimitsOptions
{
    public long PdfMaxBytes { get; set; } = 20 * 1024 * 1024; // 20MB
    public long ImageMaxBytes { get; set; } = 1 * 1024 * 1024; // 1MB
    public long AttachmentMaxBytes { get; set; } = 10 * 1024 * 1024; // 10MB
    public long RequestBodyLimitBytes { get; set; } = 40 * 1024 * 1024; // 40MB
    public int MaxConcurrentRequestsPerKey { get; set; } = 25;

    /// <summary>
    /// Pages above which an incoming PDF is refused. Page count drives
    /// rasterization cost; a byte cap alone lets a tiny file with ten thousand
    /// blank pages through.
    /// </summary>
    public int PdfMaxPages { get; set; } = 500;

    /// <summary>
    /// Refuse PDFs with a Launch action or with JavaScript that runs on open.
    /// See <c>PdfSafetyGuard</c> for what exactly counts.
    /// </summary>
    public bool RejectActiveContent { get; set; } = true;

    /// <summary>
    /// ImageMagick pixel-cache memory for find-codes rasterisation, in bytes, for the
    /// whole process. 0 = by core count: 1.5 GB below 8 cores, 2.5 GB from 8 up. Above
    /// it the rasterisation fails rather than spilling to disk (the disk limit is 0).
    /// Env <c>FIND_CODES_MAGICK_MEMORY_BYTES</c>. See <c>CodeScanRaster</c>.
    /// </summary>
    public long FindCodesMagickMemoryBytes { get; set; }

    // ── Structure of an incoming PDF (#70). Checked once by PdfSafetyGuard. ──

    /// <summary>Longest page side after /UserUnit, in points (14 400 = 5.08 m).</summary>
    public double PdfMaxPageSidePoints { get; set; } = 14_400;

    /// <summary>Objects in the cross-reference structure (iText MemoryLimitsAwareHandler).</summary>
    public int PdfMaxObjects { get; set; } = 500_000;

    /// <summary>Incremental updates, counted as %%EOF markers after the first.</summary>
    public int PdfMaxIncrementalUpdates { get; set; } = 100;

    public int PdfMaxSignatures { get; set; } = 50;
    public int PdfMaxFormFields { get; set; } = 5_000;

    /// <summary>One decompressed stream, bytes.</summary>
    public long PdfMaxStreamBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Sum of all decompressed streams, bytes.</summary>
    public long PdfMaxTotalStreamBytes { get; set; } = 256L * 1024 * 1024;

    // ── Images. Signature, logo, stamp, background: ImageMaxBytes plus these. ──

    public long ImageMaxPixels { get; set; } = 4_000_000;
    public int ImageMaxSide { get; set; } = 4_000;

    // ── PFX (#70). The iteration count is read from the file and drives the cost. ──

    public int PfxMaxBytes { get; set; } = 50 * 1024;
    public long PfxMaxIterations { get; set; } = 1_000_000;

    // ── Fill (#70). ──

    public int FillMaxDocuments { get; set; } = 50;
    public int FillMaxFields { get; set; } = 2_000;
    public long FillMaxOutputBytes { get; set; } = 100L * 1024 * 1024;
    public long FillImageMaxBytes { get; set; } = 5L * 1024 * 1024;
    public long FillImageMaxPixels { get; set; } = 25_000_000;
    public int FillImageMaxSide { get; set; } = 10_000;

    // ── Disk and storage per account (#74). 0 = no limit, the default: the portal calls this
    // server with one account for every tenant, so a per-account number that suits one customer
    // would cap all of them. Set deliberately, with the portal's account in mind. ──

    /// <summary>Presigns waiting for /api/sign, per account. Each holds a PDF of up to 20 MB in /tmp.</summary>
    public int MaxPendingPresignsPerUser { get; set; }

    /// <summary>
    /// Live (not revoked, not expired) API tokens per account (#80). Unlike the caps above it has a
    /// default: the portal holds one token, not one per tenant. 0 = no limit.
    /// </summary>
    public int MaxApiTokensPerUser { get; set; } = 100;

    /// <summary>Stored templates (up to 27 MB each), per account.</summary>
    public int MaxTemplatesPerUser { get; set; }
}
