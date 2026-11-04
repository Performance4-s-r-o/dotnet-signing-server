using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services;

/// <summary>
/// Embeds the non-embedded standard fonts (Helvetica, Times, Courier) before a PDF/A conversion
/// (#189). Each is swapped for the metric-compatible Liberation font, so glyph widths and line
/// breaks stay. Any other non-embedded font is left alone and the conversion fails as before.
/// </summary>
public static class StandardFontEmbedder
{
    private static readonly Dictionary<string, (AppFontFamily Family, bool Bold, bool Italic)> Standard = new(StringComparer.Ordinal)
    {
        ["Helvetica"] = (AppFontFamily.Sans, false, false),
        ["Helvetica-Bold"] = (AppFontFamily.Sans, true, false),
        ["Helvetica-Oblique"] = (AppFontFamily.Sans, false, true),
        ["Helvetica-BoldOblique"] = (AppFontFamily.Sans, true, true),
        ["Times-Roman"] = (AppFontFamily.Serif, false, false),
        ["Times-Bold"] = (AppFontFamily.Serif, true, false),
        ["Times-Italic"] = (AppFontFamily.Serif, false, true),
        ["Times-BoldItalic"] = (AppFontFamily.Serif, true, true),
        ["Courier"] = (AppFontFamily.Mono, false, false),
        ["Courier-Bold"] = (AppFontFamily.Mono, true, false),
        ["Courier-Oblique"] = (AppFontFamily.Mono, false, true),
        ["Courier-BoldOblique"] = (AppFontFamily.Mono, true, true),
    };

    // Every character a WinAnsi simple font can show, so the subset covers any text the page draws.
    private static readonly string WinAnsiText = BuildWinAnsiText();

    private static string BuildWinAnsiText()
    {
        var chars = new char[224];
        var encoding = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? System.Text.Encoding.Latin1;
        var bytes = new byte[224];
        for (var i = 0; i < 224; i++) bytes[i] = (byte)(32 + i);
        encoding.GetChars(bytes, 0, 224, chars, 0);
        return new string(chars);
    }

    /// <summary>The PDF with embedded fonts, or the same array when there was nothing to replace.</summary>
    public static byte[] Embed(byte[] pdf)
    {
        var output = new MemoryStream();
        var replaced = 0;
        using (var reader = new PdfReader(new MemoryStream(pdf)))
        using (var writer = new PdfWriter(output))
        {
            writer.SetCloseStream(false);
            var doc = new PdfDocument(reader, writer);
            var cache = new Dictionary<PdfDictionary, PdfFont>(ReferenceEqualityComparer.Instance as IEqualityComparer<PdfDictionary>);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance as IEqualityComparer<PdfDictionary>);
            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                var page = doc.GetPage(i);
                Walk(doc, page.GetPdfObject().GetAsDictionary(PdfName.Resources), cache, seen, ref replaced);
                var annots = page.GetPdfObject().GetAsArray(PdfName.Annots);
                for (var j = 0; annots != null && j < annots.Size(); j++)
                {
                    WalkAppearance(doc, annots.GetAsDictionary(j), cache, seen, ref replaced);
                }
            }
            var acro = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
            Walk(doc, acro?.GetAsDictionary(PdfName.DR), cache, seen, ref replaced);
            doc.Close();
        }
        return replaced == 0 ? pdf : output.ToArray();
    }

    private static void WalkAppearance(PdfDocument doc, PdfDictionary? annot, Dictionary<PdfDictionary, PdfFont> cache, HashSet<PdfDictionary> seen, ref int replaced)
    {
        var normal = annot?.GetAsDictionary(PdfName.AP)?.Get(PdfName.N);
        if (normal is PdfStream stream)
        {
            Walk(doc, stream.GetAsDictionary(PdfName.Resources), cache, seen, ref replaced, stream);
        }
        else if (normal is PdfDictionary states)
        {
            foreach (var key in states.KeySet().ToList())
            {
                if (states.Get(key) is PdfStream s) Walk(doc, s.GetAsDictionary(PdfName.Resources), cache, seen, ref replaced, s);
            }
        }
    }

    private static void Walk(PdfDocument doc, PdfDictionary? resources, Dictionary<PdfDictionary, PdfFont> cache, HashSet<PdfDictionary> seen, ref int replaced, PdfStream? owner = null)
    {
        if (resources == null || !seen.Add(resources)) return;

        var fonts = resources.GetAsDictionary(PdfName.Font);
        if (fonts != null)
        {
            foreach (var key in fonts.KeySet().ToList())
            {
                var font = fonts.GetAsDictionary(key);
                if (font == null) continue;
                if (!cache.TryGetValue(font, out var embedded))
                {
                    embedded = Replacement(doc, font)!;
                    cache[font] = embedded;
                }
                if (embedded != null)
                {
                    fonts.Put(key, embedded.GetPdfObject());
                    replaced++;
                }
            }
        }

        var xobjects = resources.GetAsDictionary(PdfName.XObject);
        if (xobjects == null) return;
        foreach (var key in xobjects.KeySet().ToList())
        {
            if (xobjects.Get(key) is PdfStream form && PdfName.Form.Equals(form.GetAsName(PdfName.Subtype)))
            {
                Walk(doc, form.GetAsDictionary(PdfName.Resources), cache, seen, ref replaced, form);
            }
        }
    }

    /// <summary>The embedded font for a non-embedded standard font in WinAnsi, else null.</summary>
    private static PdfFont? Replacement(PdfDocument doc, PdfDictionary font)
    {
        if (!PdfName.Type1.Equals(font.GetAsName(PdfName.Subtype))) return null;
        if (font.ContainsKey(PdfName.FontDescriptor)) return null;
        var name = font.GetAsName(PdfName.BaseFont)?.GetValue();
        if (name == null || !Standard.TryGetValue(name, out var spec)) return null;
        // Another encoding (Differences, MacRoman, none) would change which glyph a code draws.
        if (!PdfName.WinAnsiEncoding.Equals(font.GetAsName(PdfName.Encoding))) return null;

        var embedded = AppFonts.LoadSimple(spec.Family, spec.Bold, spec.Italic);
        embedded.SetSubset(true);
        embedded.ConvertToBytes(WinAnsiText);
        embedded.MakeIndirect(doc);
        // Fills the dictionary (descriptor, widths, font file); the page only keeps the reference.
        embedded.Flush();
        return embedded;
    }
}
