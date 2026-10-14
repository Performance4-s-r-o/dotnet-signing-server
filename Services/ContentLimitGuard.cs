using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class ContentLimitGuard
{
    private readonly LimitsOptions _options;

    /// <summary>The limits this guard enforces, for callers that need the numbers (fill: documents, fields, output).</summary>
    public LimitsOptions Limits => _options;

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
        EnsureImageDimensions(base64, _options.ImageMaxPixels, _options.ImageMaxSide);
    }

    /// <summary>
    /// Images that arrive inside fill data (the field is an image). Looser than a
    /// signature or a logo: these are photographs and scans.
    /// </summary>
    public void EnsureFillImageWithinLimit(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return;
        if (GetSizeBytes(base64) > _options.FillImageMaxBytes)
        {
            throw new ApiValidationException("IMAGE_SIZE_EXCEEDED");
        }
        EnsureImageDimensions(base64, _options.FillImageMaxPixels, _options.FillImageMaxSide);
    }

    /// <summary>
    /// PNG or JPEG only, and the size from the header before anything decodes it.
    /// Only the first bytes are decoded from base64, so a refused image costs nothing.
    /// </summary>
    private static void EnsureImageDimensions(string base64, long maxPixels, int maxSide)
    {
        // A JPEG's frame header can sit behind a large EXIF/ICC segment, so look further than a PNG needs.
        const int headBytes = 64 * 1024;
        // Whitespace is legal in base64 and does not count towards the 4-char groups.
        var compact = new string(base64.AsSpan(0, Math.Min(base64.Length, headBytes / 3 * 4 + 1024))
            .ToArray().Where(c => !char.IsWhiteSpace(c)).ToArray());
        var chars = Math.Min(compact.Length - (compact.Length % 4), headBytes / 3 * 4);
        byte[] head;
        try
        {
            head = Convert.FromBase64String(compact.Substring(0, chars));
        }
        catch (FormatException)
        {
            throw new ApiValidationException("IMAGE_FORMAT_UNSUPPORTED");
        }

        var dimensions = ImageHeader.TryRead(head);
        if (dimensions == null)
        {
            throw new ApiValidationException("IMAGE_FORMAT_UNSUPPORTED");
        }
        var (width, height) = dimensions.Value;
        if (width > maxSide || height > maxSide || (long)width * height > maxPixels)
        {
            throw new ApiValidationException("IMAGE_DIMENSIONS_EXCEEDED");
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
