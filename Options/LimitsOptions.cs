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
}
