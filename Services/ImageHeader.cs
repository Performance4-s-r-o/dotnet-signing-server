namespace DotNetSigningServer.Services;

/// <summary>
/// Reads the dimensions of a PNG or JPEG from its header, without decoding it.
/// A 30 000 × 30 000 PNG is a few kilobytes on the wire and several gigabytes once
/// an image library has expanded it, so the size has to be known BEFORE decoding.
/// Anything else (GIF, WebP, TIFF, SVG, ...) returns null: the engine accepts PNG
/// and JPEG only.
/// </summary>
public static class ImageHeader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature))
        {
            // 8 signature + 4 length + "IHDR" + width(4) + height(4)
            if (!bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
            var w = ReadInt32BigEndian(bytes[16..]);
            var h = ReadInt32BigEndian(bytes[20..]);
            return w > 0 && h > 0 ? (w, h) : null;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var i = 2;
            while (i + 4 <= bytes.Length)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                var marker = bytes[i + 1];
                if (marker == 0xFF) { i++; continue; }                  // fill byte
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; }
                var length = (bytes[i + 2] << 8) | bytes[i + 3];
                // SOF0..SOF15 except DHT (C4), JPG (C8), DAC (CC): start of frame.
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (i + 9 > bytes.Length) return null;
                    var h = (bytes[i + 5] << 8) | bytes[i + 6];
                    var w = (bytes[i + 7] << 8) | bytes[i + 8];
                    return w > 0 && h > 0 ? (w, h) : null;
                }
                if (marker == 0xDA) return null;                         // scan data before any frame header
                if (length < 2) return null;
                i += 2 + length;
            }
        }

        return null;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> b)
        => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
}
