namespace DotNetSigningServer.Options;

public class LimitsOptions
{
    public long PdfMaxBytes { get; set; } = 20 * 1024 * 1024; // 20MB
    public long ImageMaxBytes { get; set; } = 1 * 1024 * 1024; // 1MB
    public long AttachmentMaxBytes { get; set; } = 10 * 1024 * 1024; // 10MB
    public long RequestBodyLimitBytes { get; set; } = 40 * 1024 * 1024; // 40MB
    public int MaxConcurrentRequestsPerKey { get; set; } = 25;
}
