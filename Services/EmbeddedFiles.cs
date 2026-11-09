using DotNetSigningServer.Exceptions;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Filespec;

namespace DotNetSigningServer.Services;

/// <summary>
/// Embedded files in a PDF/A-3 document (#184): an attachment, or the evidence a seal carries. PDF/A-3
/// allows any file, as an associated file — the file specification has <c>/AFRelationship</c>, the
/// file stream its MIME type (<c>/Subtype</c>) and <c>/Params/ModDate</c>, and the catalog lists it in
/// <c>/AF</c>. PDF/A-1 and PDF/A-2 do not allow such files at all.
/// </summary>
public static class EmbeddedFiles
{
    /// <summary>MIME type when the file does not say; PDF/A-3 requires one.</summary>
    public const string DefaultMimeType = "application/octet-stream";

    private static readonly HashSet<PdfName> Relationships = new()
    {
        PdfName.Source, PdfName.Data, PdfName.Alternative, PdfName.Supplement, PdfName.Unspecified,
    };

    /// <summary>Code of an <c>AfRelationship</c> outside the allowed names.</summary>
    public const string RelationshipInvalidCode = "ATTACHMENT_RELATIONSHIP_INVALID";

    /// <summary>The relationship a caller asked for; empty = <c>Unspecified</c>, an unknown name throws.</summary>
    public static PdfName ParseRelationship(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return PdfName.Unspecified;
        foreach (var name in Relationships)
        {
            if (string.Equals(name.GetValue(), value.Trim(), StringComparison.OrdinalIgnoreCase)) return name;
        }
        throw new ApiValidationException(RelationshipInvalidCode, "unknown AFRelationship");
    }

    /// <summary>Embedded files of the name tree, by key.</summary>
    public static IReadOnlyList<(string Key, PdfDictionary Spec)> Of(PdfDocument document)
    {
        var names = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
        if (names?.GetAsDictionary(PdfName.EmbeddedFiles) == null) return Array.Empty<(string, PdfDictionary)>();
        var result = new List<(string, PdfDictionary)>();
        foreach (var (key, value) in document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles).GetNames())
        {
            // A broken entry is not skipped: skipping would drop the file.
            if (value is not PdfDictionary spec) throw new PdfException("embedded file entry is not a file specification");
            result.Add((key.ToUnicodeString(), spec));
        }
        return result;
    }

    /// <summary>
    /// Copies every embedded file of <paramref name="source"/> into <paramref name="target"/> as an
    /// associated file, byte for byte under the same key, name and description. A file without
    /// content fails the conversion (<see cref="PdfException"/>, refused as a failed conversion)
    /// instead of being left out.
    /// </summary>
    public static void CarryOver(PdfDocument source, PdfDocument target)
    {
        foreach (var (key, spec) in Of(source))
        {
            var ef = spec.GetAsDictionary(PdfName.EF);
            var stream = ef?.GetAsStream(PdfName.UF) ?? ef?.GetAsStream(PdfName.F)
                ?? throw new PdfException("embedded file without content");
            var bytes = stream.GetBytes();
            var fileName = spec.GetAsString(PdfName.UF)?.ToUnicodeString() ?? spec.GetAsString(PdfName.F)?.ToUnicodeString() ?? key;
            var relationship = spec.GetAsName(PdfName.AFRelationship);
            var modDate = stream.GetAsDictionary(PdfName.Params)?.GetAsString(PdfName.ModDate);
            Add(target, key, bytes, spec.GetAsString(PdfName.Desc)?.ToUnicodeString(), fileName,
                stream.GetAsName(PdfName.Subtype)?.GetValue(), relationship != null && Relationships.Contains(relationship) ? relationship : null,
                modDate);
        }
    }

    /// <summary>Adds one file as an associated file of the document (catalog <c>/AF</c> and the name tree).</summary>
    public static PdfFileSpec Add(PdfDocument document, string key, byte[] bytes, string? description, string fileName,
        string? mimeType, PdfName? relationship, PdfString? modDate = null)
    {
        var parameters = new PdfDictionary();
        parameters.Put(PdfName.ModDate, modDate ?? new PdfDate().GetPdfObject());
        var spec = PdfFileSpec.CreateEmbeddedFileSpec(document, bytes, description, fileName,
            new PdfName(string.IsNullOrWhiteSpace(mimeType) ? DefaultMimeType : mimeType), parameters, relationship ?? PdfName.Unspecified);
        var catalog = document.GetCatalog();
        var af = catalog.GetPdfObject().GetAsArray(PdfName.AF);
        if (af == null)
        {
            af = new PdfArray();
            af.MakeIndirect(document);
            catalog.Put(PdfName.AF, af);
        }
        af.Add(spec.GetPdfObject());
        af.SetModified();
        document.AddFileAttachment(key, spec);
        return spec;
    }

    /// <summary>
    /// What keeps the document from being PDF/A of <paramref name="part"/> because of its embedded
    /// files: none allowed below part 3 (whether a file inside is itself PDF/A is not checked, so any
    /// file counts), and in part 3 each must be an associated file with a MIME type.
    /// </summary>
    public static bool Conform(PdfDocument document, string part)
    {
        var files = Of(document);
        if (files.Count == 0) return true;
        if (part != "3") return false;
        var af = document.GetCatalog().GetPdfObject().GetAsArray(PdfName.AF);
        var associated = new HashSet<PdfIndirectReference>();
        if (af != null)
        {
            for (var i = 0; i < af.Size(); i++)
            {
                if (af.Get(i, false) is PdfIndirectReference reference) associated.Add(reference);
                else if (af.GetAsDictionary(i)?.GetIndirectReference() is { } r) associated.Add(r);
            }
        }
        foreach (var (_, spec) in files)
        {
            var reference = spec.GetIndirectReference();
            var ef = spec.GetAsDictionary(PdfName.EF);
            var stream = ef?.GetAsStream(PdfName.F) ?? ef?.GetAsStream(PdfName.UF);
            if (reference == null || !associated.Contains(reference)) return false;
            if (spec.GetAsName(PdfName.AFRelationship) == null) return false;
            if (stream?.GetAsName(PdfName.Subtype) == null) return false;
        }
        return true;
    }
}
