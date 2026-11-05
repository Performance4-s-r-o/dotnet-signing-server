using System.Buffers.Binary;
using System.IO.Compression;
using DotNetSigningServer.Exceptions;
using ImageMagick;
using iText.Kernel.Colors;
using iText.Kernel.Exceptions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace DotNetSigningServer.Services.Ai;

/// <summary>
/// The part of a document an AI call may read: a page range cut to the limit, the text layer of each
/// page (to check the model's evidence against) and whether it carries text a person cannot see.
/// </summary>
/// <param name="Pdf">A new PDF of just the selected pages.</param>
/// <param name="FirstPage">1-based page of the original that is page 1 of <paramref name="Pdf"/>.</param>
/// <param name="PageTexts">Text layer of each selected page.</param>
/// <param name="HiddenText">Invisible, tiny, background-coloured or off-page text was found.</param>
public sealed record AiDocument(byte[] Pdf, int FirstPage, int TotalPages, IReadOnlyList<string> PageTexts, bool HiddenText, bool Truncated)
{
    public const string InvalidPageRangeCode = "AI_INVALID_PAGE_RANGE";

    /// <summary>Text under this size (points) counts as hidden.</summary>
    public const float MinVisibleTextSize = 2f;

    /// <summary>Fill lightness from which text counts as background-coloured (a white page assumed).</summary>
    public const float BackgroundLightness = 0.95f;

    /// <summary>Resolution of the page images sent instead of the PDF when hidden text was found.</summary>
    public const double RasterDpi = 150;

    public int PageCount => PageTexts.Count;

    /// <summary>Selects the pages: <paramref name="pageFrom"/>–<paramref name="pageTo"/> (default all), at most <paramref name="maxPages"/>.</summary>
    public static AiDocument Prepare(byte[] pdf, int? pageFrom, int? pageTo, int maxPages)
    {
        if (pageFrom is < 1 || pageTo is < 1 || (pageFrom is { } f && pageTo is { } t && t < f))
        {
            throw new ApiValidationException(InvalidPageRangeCode, "pageFrom and pageTo must be positive and pageFrom ≤ pageTo");
        }
        try
        {
            using var input = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var total = input.GetNumberOfPages();
            var from = pageFrom ?? 1;
            if (from > total) throw new ApiValidationException(InvalidPageRangeCode, $"pageFrom {from} is past the last page {total}");
            var to = Math.Min(pageTo ?? total, total);
            var truncated = false;
            var limit = Math.Max(1, maxPages);
            if (to - from + 1 > limit)
            {
                to = from + limit - 1;
                truncated = true;
            }

            using var output = new MemoryStream();
            using (var selected = new PdfDocument(new PdfWriter(output)))
            {
                input.CopyPagesTo(from, to, selected);
            }
            var bytes = output.ToArray();

            var texts = new List<string>();
            var hidden = false;
            using (var copy = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
            {
                for (var i = 1; i <= copy.GetNumberOfPages(); i++)
                {
                    var page = copy.GetPage(i);
                    texts.Add(PdfTextExtractor.GetTextFromPage(page, new LocationTextExtractionStrategy()));
                    if (!hidden)
                    {
                        var listener = new HiddenTextListener(page.GetCropBox());
                        new PdfCanvasProcessor(listener).ProcessPageContent(page);
                        hidden = listener.Found;
                    }
                }
            }
            return new AiDocument(bytes, from, total, texts, hidden, truncated);
        }
        catch (BadPasswordException)
        {
            throw new ApiValidationException("PDF_ENCRYPTED", "Password protected");
        }
        catch (Exception ex) when (ex is PdfException or IOException)
        {
            throw new ApiValidationException("PDF_INVALID", ex.Message);
        }
    }

    /// <summary>The page as the original numbers it, or null when <paramref name="page"/> is not one of the selected pages.</summary>
    public int? OriginalPage(int? page) => page is { } p && p >= 1 && p <= PageCount ? FirstPage + p - 1 : null;

    /// <summary>Whether <paramref name="evidence"/> occurs in the text layer of <paramref name="page"/> (1-based, selected pages).</summary>
    public bool EvidenceOnPage(int? page, string? evidence)
    {
        if (page is not { } p || p < 1 || p > PageCount || string.IsNullOrWhiteSpace(evidence)) return false;
        var needle = Normalize(evidence);
        return needle.Length > 0 && Normalize(PageTexts[p - 1]).Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>Case and whitespace do not matter: extraction spaces text differently from the model's quote.</summary>
    internal static string Normalize(string text) =>
        new string(text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Each selected page as a grey PNG at <see cref="RasterDpi"/> (or lower for a large page): only what a person sees.</summary>
    public async Task<IReadOnlyList<byte[]>> RasterizeAsync(CancellationToken cancellationToken)
    {
        var dpis = new List<double>();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(Pdf))))
        {
            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                var box = doc.GetPage(i).GetMediaBox();
                var dpi = CodeScanRaster.DpiFor(box.GetWidth(), box.GetHeight())
                    ?? throw new ApiValidationException("PDF_PAGE_TOO_LARGE", $"Page {i} is too large to rasterise");
                dpis.Add(Math.Min(RasterDpi, dpi));
            }
        }
        var clean = CodeScanRaster.Reserialize(Pdf);
        var pages = new List<byte[]>(dpis.Count);
        for (var i = 0; i < dpis.Count; i++)
        {
            pages.Add(await CodeScanRaster.WithPageAsync(clean, i, dpis[i],
                image => GrayPng.Encode(image.ToByteArray(MagickFormat.Gray), (int)image.Width, (int)image.Height), cancellationToken));
        }
        return pages;
    }

    /// <summary>
    /// Text a person does not see: render mode 3 (invisible) or 7 (clip only), smaller than
    /// <see cref="MinVisibleTextSize"/>, filled in (near) white, or placed outside the CropBox.
    /// </summary>
    private sealed class HiddenTextListener(Rectangle cropBox) : IEventListener
    {
        public bool Found { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (Found || data is not TextRenderInfo info || string.IsNullOrWhiteSpace(info.GetText())) return;
            var mode = info.GetTextRenderMode();
            if (mode is 3 or 7) { Found = true; return; }

            var ascent = info.GetAscentLine().GetStartPoint();
            var descent = info.GetDescentLine().GetStartPoint();
            var height = ascent.Subtract(descent).Length();
            if (height < MinVisibleTextSize) { Found = true; return; }

            // Mode 1 strokes only: the stroke colour is what shows.
            var color = mode == 1 ? info.GetStrokeColor() : info.GetFillColor();
            if (Lightness(color) >= BackgroundLightness) { Found = true; return; }

            var start = info.GetBaseline().GetStartPoint();
            var end = info.GetBaseline().GetEndPoint();
            if (!Inside(start) || !Inside(end)) Found = true;
        }

        private bool Inside(Vector point)
        {
            const float tolerance = 1f;
            var x = point.Get(Vector.I1);
            var y = point.Get(Vector.I2);
            return x >= cropBox.GetLeft() - tolerance && x <= cropBox.GetRight() + tolerance
                && y >= cropBox.GetBottom() - tolerance && y <= cropBox.GetTop() + tolerance;
        }

        private static float Lightness(Color? color)
        {
            var v = color?.GetColorValue();
            if (v is null || v.Length == 0) return 0;
            return color switch
            {
                DeviceGray => v[0],
                DeviceRgb => 0.299f * v[0] + 0.587f * v[1] + 0.114f * v[2],
                DeviceCmyk => (1 - v[0]) * (1 - v[3]) * 0.299f + (1 - v[1]) * (1 - v[3]) * 0.587f + (1 - v[2]) * (1 - v[3]) * 0.114f,
                _ => 0,
            };
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }
}

/// <summary>A minimal 8-bit greyscale PNG writer (ImageMagick may only write raw grey here).</summary>
internal static class GrayPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(byte[] gray, int width, int height)
    {
        if (gray.Length < (long)width * height) throw new ArgumentException("Pixel data shorter than the image", nameof(gray));
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        Chunk(output, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(gray, y * width, width);
                }
            }
            Chunk(output, "IDAT", raw.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
