using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services;

/// <summary>
/// A minimal check of a document that is meant to be PDF/A (#184). It is not a validator: iText
/// checks while it writes (output intent, metadata, colours, transparency, actions, annotations of
/// the pages it writes), but it does not look at the fonts of content it only copies — a document
/// with a non-embedded font came out of the conversion claiming PDF/A-2B and failed veraPDF. This
/// covers that gap and the declaration itself:
///
/// <list type="bullet">
/// <item>the XMP declares the expected part and level (<c>pdfaid</c>);</item>
/// <item>every font reachable from the pages (content, form XObjects, patterns, Type3 glyphs,
/// annotation appearances) is embedded;</item>
/// <item>embedded files: none below PDF/A-3, associated files with a MIME type in PDF/A-3
/// (<see cref="EmbeddedFiles.Conform"/>).</item>
/// </list>
///
/// What it does not see is listed in README.md (presign with PDF/A conversion).
/// </summary>
public static class PdfAConformanceCheck
{
    public const string NotDeclared = "pdfa-not-declared";
    public const string FontNotEmbedded = "font-not-embedded";
    public const string EmbeddedFileNotAllowed = "embedded-file-not-allowed";

    private const int MaxDepth = 32;

    /// <summary>The problems found, empty when none. Kinds only, never names from the document.</summary>
    public static IReadOnlyList<string> Problems(byte[] pdf, PdfAConformance expected)
    {
        PdfOpenCounter.Record();
        using var document = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Problems(document, expected);
    }

    public static IReadOnlyList<string> Problems(PdfDocument document, PdfAConformance expected)
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var declared = document.GetConformance().GetAConformance();
        if (declared == null || declared.GetPart() != expected.GetPart() || declared.GetLevel() != expected.GetLevel())
        {
            problems.Add(NotDeclared);
        }

        if (!EmbeddedFiles.Conform(document, expected.GetPart())) problems.Add(EmbeddedFileNotAllowed);

        var seen = new HashSet<PdfIndirectReference>();
        for (var i = 1; i <= document.GetNumberOfPages(); i++)
        {
            var page = document.GetPage(i).GetPdfObject();
            if (!CheckResources(page.GetAsDictionary(PdfName.Resources), seen, 0)) problems.Add(FontNotEmbedded);
            var annots = page.GetAsArray(PdfName.Annots);
            if (annots == null) continue;
            foreach (var item in annots)
            {
                if (item is PdfDictionary annot && !CheckAppearance(annot.GetAsDictionary(PdfName.AP), seen, 0))
                {
                    problems.Add(FontNotEmbedded);
                }
            }
        }
        return problems.ToList();
    }

    /// <summary>Declared PDF/A part and level as in <see cref="PdfConversionService.FormatConformance"/>, or null.</summary>
    public static string? Declared(PdfDocument document)
    {
        var declared = document.GetConformance().GetAConformance();
        return declared == null ? null : $"PDF/A-{declared.GetPart()}{declared.GetLevel()}";
    }

    private static bool CheckAppearance(PdfDictionary? ap, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (ap == null || depth > MaxDepth) return true;
        var ok = true;
        foreach (var key in new[] { PdfName.N, PdfName.R, PdfName.D })
        {
            var entry = ap.Get(key);
            if (entry is PdfStream stream)
            {
                ok &= CheckXObject(stream, seen, depth + 1);
            }
            else if (entry is PdfDictionary states)
            {
                foreach (var state in states.Values())
                {
                    if (state is PdfStream s) ok &= CheckXObject(s, seen, depth + 1);
                }
            }
        }
        return ok;
    }

    private static bool CheckXObject(PdfStream stream, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (!FirstVisit(stream, seen)) return true;
        return CheckResources(stream.GetAsDictionary(PdfName.Resources), seen, depth + 1);
    }

    private static bool CheckResources(PdfDictionary? resources, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (resources == null) return true;
        // Deeper than this is not a document anyone made on purpose; refuse rather than pass unseen.
        if (depth > MaxDepth) return false;
        var ok = true;

        var fonts = resources.GetAsDictionary(PdfName.Font);
        if (fonts != null)
        {
            foreach (var item in fonts.Values())
            {
                if (item is PdfDictionary font) ok &= CheckFont(font, seen, depth);
            }
        }

        foreach (var kind in new[] { PdfName.XObject, PdfName.Pattern })
        {
            var dict = resources.GetAsDictionary(kind);
            if (dict == null) continue;
            foreach (var item in dict.Values())
            {
                // Form XObjects and tiling patterns carry resources; images and shading patterns do not.
                if (item is PdfStream stream && !PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                {
                    ok &= CheckXObject(stream, seen, depth);
                }
            }
        }
        return ok;
    }

    private static bool CheckFont(PdfDictionary font, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (!FirstVisit(font, seen)) return true;
        var subtype = font.GetAsName(PdfName.Subtype);
        if (PdfName.Type3.Equals(subtype))
        {
            // Glyphs are content streams; their own resources can name fonts.
            return CheckResources(font.GetAsDictionary(PdfName.Resources), seen, depth + 1);
        }
        if (PdfName.Type0.Equals(subtype))
        {
            var descendants = font.GetAsArray(PdfName.DescendantFonts);
            if (descendants == null || descendants.Size() == 0) return false;
            var descendant = descendants.GetAsDictionary(0);
            return descendant != null && HasFontFile(descendant.GetAsDictionary(PdfName.FontDescriptor));
        }
        return HasFontFile(font.GetAsDictionary(PdfName.FontDescriptor));
    }

    private static bool HasFontFile(PdfDictionary? descriptor) =>
        descriptor != null
        && (descriptor.ContainsKey(PdfName.FontFile) || descriptor.ContainsKey(PdfName.FontFile2) || descriptor.ContainsKey(PdfName.FontFile3));

    private static bool FirstVisit(PdfObject obj, HashSet<PdfIndirectReference> seen)
    {
        var reference = obj.GetIndirectReference();
        return reference == null || seen.Add(reference);
    }
}
