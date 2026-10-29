using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Signatures;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Opens an incoming PDF before any work is done on it and refuses what the
/// engine could not process, or should not pass on.
///
/// This is the thorough check; the portal does a cheap one (base64, size,
/// header) before forwarding. Four things are refused:
///
/// - <c>PDF_INVALID</c>: not base64, no <c>%PDF-</c> header within the first
///   1024 bytes, or iText cannot parse it.
/// - <c>PDF_ENCRYPTED</c>: any encryption, even owner-password only. Signing
///   or sealing an encrypted file fails later in a less helpful place.
/// - <c>PDF_TOO_MANY_PAGES</c>: above <see cref="LimitsOptions.PdfMaxPages"/>.
///   Page count is what drives rasterization cost; a size cap alone lets a
///   tiny file with ten thousand blank pages through.
/// - <c>PDF_ACTIVE_CONTENT</c>: a <c>/Launch</c> action anywhere reachable,
///   or JavaScript that runs on open (catalog <c>/OpenAction</c>, catalog
///   <c>/AA</c>, the <c>/JavaScript</c> name tree), on a page (<c>/AA</c>) or
///   behind a link annotation. Scripts on FORM FIELDS are deliberately allowed:
///   format and calculation scripts are how interactive forms are built, and
///   refusing them would refuse most government forms.
///
/// Embedded files are NOT refused. The evidence this server attaches to every
/// sealed document is an embedded file, and so is the XML inside a PDF/A-3
/// invoice.
/// </summary>
public class PdfSafetyGuard
{
    private const int HeaderWindow = 1024;
    private static readonly byte[] Header = "%PDF-"u8.ToArray();

    private readonly LimitsOptions _options;

    public PdfSafetyGuard(IOptions<LimitsOptions> options)
    {
        _options = options.Value;
    }

    public int EnsureSafePdf(string? base64, string context, bool checkActiveContent = true)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new ApiValidationException("PDF_INVALID", $"{context}: no content");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new ApiValidationException("PDF_INVALID", $"{context}: not base64");
        }

        return EnsureSafePdf(bytes, context, checkActiveContent);
    }

    /// <param name="checkActiveContent">
    /// False for read-only endpoints (signature inspection): scripts do no harm to a
    /// document that is only read, and refusing them would refuse verifying it.
    /// The structural limits apply either way.
    /// </param>
    /// <returns>The page count, read from the one parse done here (#70).</returns>
    public int EnsureSafePdf(byte[] bytes, string context, bool checkActiveContent = true)
    {
        if (bytes.LongLength > _options.PdfMaxBytes)
        {
            throw new ApiValidationException("PDF_SIZE_EXCEEDED", $"{context}: {bytes.LongLength} bytes");
        }
        if (!HasPdfHeader(bytes))
        {
            throw new ApiValidationException("PDF_INVALID", $"{context}: no PDF header");
        }
        var updates = CountIncrementalUpdates(bytes);
        if (updates > _options.PdfMaxIncrementalUpdates)
        {
            throw new ApiValidationException(
                "PDF_TOO_COMPLEX",
                $"{context}: {updates} incremental updates, limit {_options.PdfMaxIncrementalUpdates}");
        }

        PdfDocument document;
        PdfReader reader;
        try
        {
            // Bounds what a hostile file can make the parser allocate. The default
            // handler has limits too, but far above what this server has memory for.
            var handler = new MemoryLimitsAwareHandler();
            handler.SetMaxSizeOfSingleDecompressedPdfStream((int)Math.Min(_options.PdfMaxStreamBytes, int.MaxValue));
            handler.SetMaxSizeOfDecompressedPdfStreamsSum(_options.PdfMaxTotalStreamBytes);
            handler.SetMaxNumberOfElementsInXrefStructure(_options.PdfMaxObjects);
            PdfOpenCounter.Record();
            reader = new PdfReader(new MemoryStream(bytes), new ReaderProperties().SetMemoryLimitsAwareHandler(handler));
            document = new PdfDocument(reader);
        }
        catch (BadPasswordException)
        {
            throw new ApiValidationException("PDF_ENCRYPTED", $"{context}: password protected");
        }
        catch (MemoryLimitsAwareException)
        {
            throw new ApiValidationException("PDF_TOO_COMPLEX", $"{context}: structure above the limits");
        }
        catch (Exception ex) when (ex is PdfException || ex is IOException)
        {
            throw new ApiValidationException("PDF_INVALID", $"{context}: {ex.Message}");
        }

        using (document)
        {
            // Owner-password-only files open for reading, but every write path
            // (seal, sign, fill) refuses them further down with a worse message.
            if (reader.IsEncrypted())
            {
                throw new ApiValidationException("PDF_ENCRYPTED", $"{context}: encrypted");
            }

            var pages = document.GetNumberOfPages();
            if (pages > _options.PdfMaxPages)
            {
                throw new ApiValidationException(
                    "PDF_TOO_MANY_PAGES",
                    $"{context}: {pages} pages, limit {_options.PdfMaxPages}");
            }

            EnsureStructure(document, context);

            if (checkActiveContent && _options.RejectActiveContent)
            {
                var offender = FindActiveContent(document);
                if (offender != null)
                {
                    throw new ApiValidationException("PDF_ACTIVE_CONTENT", $"{context}: {offender}");
                }
            }

            return pages;
        }
    }

    private void EnsureStructure(PdfDocument document, string context)
    {
        try
        {
            var objects = document.GetNumberOfPdfObjects();
            if (objects > _options.PdfMaxObjects)
            {
                throw new ApiValidationException("PDF_TOO_COMPLEX", $"{context}: {objects} objects, limit {_options.PdfMaxObjects}");
            }

            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                var box = page.GetMediaBox();
                var unit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1.0;
                if (unit <= 0) unit = 1.0;
                var side = Math.Max(Math.Abs(box.GetWidth()), Math.Abs(box.GetHeight())) * unit;
                if (side > _options.PdfMaxPageSidePoints)
                {
                    throw new ApiValidationException(
                        "PDF_PAGE_TOO_LARGE",
                        $"{context}: page {i} side {side:0} pt, limit {_options.PdfMaxPageSidePoints:0}");
                }
            }

            var signatures = new SignatureUtil(document).GetSignatureNames().Count;
            if (signatures > _options.PdfMaxSignatures)
            {
                throw new ApiValidationException("PDF_TOO_COMPLEX", $"{context}: {signatures} signatures, limit {_options.PdfMaxSignatures}");
            }

            var fields = document.GetCatalog().GetPdfObject()
                .GetAsDictionary(PdfName.AcroForm)?.GetAsArray(PdfName.Fields);
            if (fields != null)
            {
                var count = 0;
                if (!CountFields(fields, ref count, _options.PdfMaxFormFields, depth: 0, new HashSet<PdfIndirectReference>()))
                {
                    throw new ApiValidationException("PDF_TOO_COMPLEX", $"{context}: more than {_options.PdfMaxFormFields} form fields");
                }
            }

            // Decompress every stream once with the limits in force. A stream bomb is a few
            // kilobytes of deflate that expands to gigabytes; nothing earlier in this method
            // reads a stream, so without this the bomb goes off in a later service instead,
            // which has no limits.
            for (var n = 1; n <= objects; n++)
            {
                if (document.GetPdfObject(n) is not PdfStream stream) continue;
                try
                {
                    stream.GetBytes(true);
                }
                catch (MemoryLimitsAwareException)
                {
                    throw;
                }
                catch (PdfException)
                {
                    // A corrupt stream is the downstream service's problem, not a limit.
                }
            }
        }
        catch (MemoryLimitsAwareException)
        {
            throw new ApiValidationException("PDF_TOO_COMPLEX", $"{context}: structure above the limits");
        }
        catch (Exception ex) when (ex is PdfException || ex is IOException)
        {
            // e.g. "Invalid page structure", a cycle in /Kids: the caller's file, not our fault.
            throw new ApiValidationException("PDF_INVALID", $"{context}: {ex.Message}");
        }
    }

    /// <summary>Counts fields, stopping as soon as the limit is passed. False = over the limit.</summary>
    private static bool CountFields(PdfArray fields, ref int count, int limit, int depth, HashSet<PdfIndirectReference> seen)
    {
        if (depth > 32) return true;
        for (var i = 0; i < fields.Size(); i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null) continue;
            var reference = field.GetIndirectReference();
            if (reference != null && !seen.Add(reference)) continue;
            if (++count > limit) return false;
            var kids = field.GetAsArray(PdfName.Kids);
            if (kids != null && !CountFields(kids, ref count, limit, depth + 1, seen)) return false;
        }
        return true;
    }

    /// <summary>One %%EOF per revision; the first is the file itself.</summary>
    private static int CountIncrementalUpdates(byte[] bytes)
    {
        var marker = "%%EOF"u8;
        var count = 0;
        var span = bytes.AsSpan();
        var from = 0;
        while (from < span.Length)
        {
            var at = span[from..].IndexOf(marker);
            if (at < 0) break;
            count++;
            from += at + marker.Length;
        }
        return Math.Max(0, count - 1);
    }

    private static bool HasPdfHeader(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length - Header.Length, HeaderWindow);
        for (var i = 0; i <= limit; i++)
        {
            if (bytes.AsSpan(i, Header.Length).SequenceEqual(Header)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a short description of the first piece of active content found,
    /// or null when the document is clean.
    /// </summary>
    private static string? FindActiveContent(PdfDocument document)
    {
        var catalog = document.GetCatalog().GetPdfObject();
        var visited = new HashSet<PdfIndirectReference>();

        // Document-level: runs when the file is opened.
        var fromCatalog = InspectAction(catalog.Get(PdfName.OpenAction), "OpenAction", visited, allowJavaScript: false)
            ?? InspectAdditionalActions(catalog.GetAsDictionary(PdfName.AA), "document", visited, allowJavaScript: false)
            ?? InspectJavaScriptNameTree(catalog, visited);
        if (fromCatalog != null) return fromCatalog;

        for (var i = 1; i <= document.GetNumberOfPages(); i++)
        {
            var page = document.GetPage(i).GetPdfObject();
            var fromPage = InspectAdditionalActions(page.GetAsDictionary(PdfName.AA), $"page {i}", visited, allowJavaScript: false);
            if (fromPage != null) return fromPage;

            var annots = page.GetAsArray(PdfName.Annots);
            if (annots == null) continue;
            for (var a = 0; a < annots.Size(); a++)
            {
                var annot = annots.GetAsDictionary(a);
                if (annot == null) continue;
                var subtype = annot.GetAsName(PdfName.Subtype);
                // Widgets carry form-field scripts (format, validate, calculate):
                // allowed. Everything else — links, screen annotations — is not a
                // place a script has any business being.
                var isWidget = PdfName.Widget.Equals(subtype);
                var found = InspectAction(annot.Get(PdfName.A), $"annotation on page {i}", visited, allowJavaScript: isWidget)
                    ?? InspectAdditionalActions(annot.GetAsDictionary(PdfName.AA), $"annotation on page {i}", visited, allowJavaScript: isWidget);
                if (found != null) return found;
            }
        }

        // Field-level scripts on fields without their own widget entry.
        var acroForm = catalog.GetAsDictionary(PdfName.AcroForm);
        var fields = acroForm?.GetAsArray(PdfName.Fields);
        if (fields != null)
        {
            var found = InspectFields(fields, visited, depth: 0);
            if (found != null) return found;
        }

        return null;
    }

    private static string? InspectFields(PdfArray fields, HashSet<PdfIndirectReference> visited, int depth)
    {
        if (depth > 32) return null;
        for (var i = 0; i < fields.Size(); i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null) continue;
            var reference = field.GetIndirectReference();
            if (reference != null && !visited.Add(reference)) continue;

            // Launch is never fine, JavaScript on a field is.
            var found = InspectAction(field.Get(PdfName.A), "form field", visited, allowJavaScript: true)
                ?? InspectAdditionalActions(field.GetAsDictionary(PdfName.AA), "form field", visited, allowJavaScript: true);
            if (found != null) return found;

            var kids = field.GetAsArray(PdfName.Kids);
            if (kids != null)
            {
                found = InspectFields(kids, visited, depth + 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static string? InspectJavaScriptNameTree(PdfDictionary catalog, HashSet<PdfIndirectReference> visited)
    {
        var names = catalog.GetAsDictionary(PdfName.Names);
        var tree = names?.GetAsDictionary(PdfName.JavaScript);
        if (tree == null) return null;
        // A JavaScript name tree exists only to run scripts on open. Its mere
        // presence is the finding; walking it would only tell us how many.
        return "document-level JavaScript";
    }

    private static string? InspectAdditionalActions(
        PdfDictionary? aa,
        string where,
        HashSet<PdfIndirectReference> visited,
        bool allowJavaScript)
    {
        if (aa == null) return null;
        foreach (var key in aa.KeySet())
        {
            var found = InspectAction(aa.Get(key), $"{where} ({key})", visited, allowJavaScript);
            if (found != null) return found;
        }
        return null;
    }

    private static string? InspectAction(
        PdfObject? action,
        string where,
        HashSet<PdfIndirectReference> visited,
        bool allowJavaScript)
    {
        // An OpenAction may be a destination array — a plain "go to page", fine.
        if (action is not PdfDictionary dictionary) return null;
        var reference = dictionary.GetIndirectReference();
        if (reference != null && !visited.Add(reference)) return null;

        var kind = dictionary.GetAsName(PdfName.S);
        if (PdfName.Launch.Equals(kind))
        {
            return $"Launch action in {where}";
        }
        if (!allowJavaScript && (PdfName.JavaScript.Equals(kind) || dictionary.ContainsKey(PdfName.JS)))
        {
            return $"JavaScript in {where}";
        }

        // Actions chain: /Next is a single action or an array of them.
        var next = dictionary.Get(PdfName.Next);
        if (next is PdfArray chain)
        {
            for (var i = 0; i < chain.Size(); i++)
            {
                var found = InspectAction(chain.Get(i), where, visited, allowJavaScript);
                if (found != null) return found;
            }
        }
        else if (next != null)
        {
            return InspectAction(next, where, visited, allowJavaScript);
        }

        return null;
    }
}
