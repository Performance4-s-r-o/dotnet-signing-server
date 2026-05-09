using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class ContentLimitGuard
{
    private readonly LimitsOptions _options;

    public ContentLimitGuard(IOptions<LimitsOptions> options)
    {
        _options = options.Value;
    }

    public void EnsurePdfWithinLimit(string base64, string context)
        => EnsureWithinLimit(base64, _options.PdfMaxBytes, $"{context} PDF");

    public void EnsureImageWithinLimit(string? base64, string context)
    {
        if (string.IsNullOrWhiteSpace(base64)) return;
        EnsureWithinLimit(base64, _options.ImageMaxBytes, $"{context} image");
    }

    public void EnsureAttachmentWithinLimit(string base64, string context)
        => EnsureWithinLimit(base64, _options.AttachmentMaxBytes, $"{context} attachment");

    public long GetSizeBytes(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return 0;
        var padding = 0;
        if (base64.EndsWith("==")) padding = 2;
        else if (base64.EndsWith("=")) padding = 1;
        return (long)(base64.Length * 3 / 4) - padding;
    }

    private void EnsureWithinLimit(string base64, long limitBytes, string context)
    {
        var size = GetSizeBytes(base64);
        if (size > limitBytes)
        {
            throw new InvalidOperationException($"{context} exceeds the allowed size of {limitBytes / (1024 * 1024)} MB.");
        }
    }
}
