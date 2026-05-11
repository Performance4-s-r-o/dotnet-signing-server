using DotNetSigningServer.Exceptions;
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
    {
        var size = GetSizeBytes(base64);
        if (size > _options.PdfMaxBytes)
        {
            throw new ApiValidationException("PDF_SIZE_EXCEEDED");
        }
    }

    public void EnsureImageWithinLimit(string? base64, string context)
    {
        if (string.IsNullOrWhiteSpace(base64)) return;
        var size = GetSizeBytes(base64);
        if (size > _options.ImageMaxBytes)
        {
            throw new ApiValidationException("IMAGE_SIZE_EXCEEDED");
        }
    }

    public void EnsureAttachmentWithinLimit(string base64, string context)
    {
        var size = GetSizeBytes(base64);
        if (size > _options.AttachmentMaxBytes)
        {
            throw new ApiValidationException("ATTACHMENT_SIZE_EXCEEDED");
        }
    }

    public long GetSizeBytes(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return 0;
        var padding = 0;
        if (base64.EndsWith("==")) padding = 2;
        else if (base64.EndsWith("=")) padding = 1;
        return (long)(base64.Length * 3 / 4) - padding;
    }
}
