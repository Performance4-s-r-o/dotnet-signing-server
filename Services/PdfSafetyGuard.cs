using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
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

    public void EnsureSafePdf(string? base64, string context)
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

        EnsureSafePdf(bytes, context);
    }

    public void EnsureSafePdf(byte[] bytes, string context)
    {
        if (!HasPdfHeader(bytes))
        {
            throw new ApiValidationException("PDF_INVALID", $"{context}: no PDF header");
        }

        PdfDocument document;
        PdfReader reader;
        try
        {
            reader = new PdfReader(new MemoryStream(bytes));
            document = new PdfDocument(reader);
        }
        catch (BadPasswordException)
        {
            throw new ApiValidationException("PDF_ENCRYPTED", $"{context}: password protected");
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

            if (_options.RejectActiveContent)
            {
                var offender = FindActiveContent(document);
                if (offender != null)
                {
                    throw new ApiValidationException("PDF_ACTIVE_CONTENT", $"{context}: {offender}");
                }
            }
        }
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
