using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Pdfa.Exceptions;

namespace DotNetSigningServer.Services;

/// <summary>
/// What presign, timestamp and visual-sign do to the document before it is signed (#184), when
/// asked: (1) flattening the form and removing active content — the step of <c>/api/flatten-form</c>,
/// skipped on a signed document; (2) conversion to PDF/A, checked afterwards. One step for all three
/// routes. Nothing falls back to the unconverted document: a document that cannot be converted is
/// refused. The signature written afterwards must keep the PDF/A (<see cref="KeepPdfA{T}"/>,
/// <see cref="EnsurePreserved"/>).
/// </summary>
public static class DocumentPreparation
{
    public const string ConversionFailedCode = "PDFA_CONVERSION_FAILED";
    public const string NotPreservedCode = "PDFA_NOT_PRESERVED";
    public const string ConformanceUnsupportedCode = "PDFA_CONFORMANCE_UNSUPPORTED";
    /// <summary>Embedded files (an attachment, the evidence of a seal) and a PDF/A part that does not allow them.</summary>
    public const string EmbeddedFilesCode = "PDFA_EMBEDDED_FILES";

    /// <summary>Default and only supported targets: level A needs real tagging and level U Unicode maps the conversion cannot add.</summary>
    public const string DefaultConformance = "PDF/A-2B";

    public static class FlattenOutcome
    {
        public const string Flattened = "flattened";
        public const string Unchanged = "unchanged";
        public const string SkippedSigned = "skipped-signed";
    }

    public static class PdfaOutcome
    {
        public const string Converted = "converted";
        /// <summary>Signed and already declaring PDF/A: converting would break the signature, the new one is added incrementally.</summary>
        public const string AlreadyPdfa = "already-pdfa";
    }

    public sealed record Report(
        string? Flatten,
        int FlattenedFields,
        IReadOnlyList<string> Removed,
        string? Pdfa,
        string? Conformance);

    /// <param name="Pdf">The prepared document.</param>
    /// <param name="Pages">Its page count, for the price of the conversion.</param>
    /// <param name="KeepPdfA">Set when the result is PDF/A: the signature must keep it so.</param>
    public sealed record Result(byte[] Pdf, int Pages, Report Report, PdfAConformance? KeepPdfA);

    /// <summary>The supported target for a requested value; empty = the default. Anything else is refused, never mapped to a default.</summary>
    public static (string Name, PdfAConformance Conformance) ResolveConformance(string? requested)
    {
        var key = new string((requested ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return key switch
        {
            "" or "PDFA2B" => ("PDF/A-2B", PdfAConformance.PDF_A_2B),
            "PDFA3B" => ("PDF/A-3B", PdfAConformance.PDF_A_3B),
            _ => throw new ApiValidationException(ConformanceUnsupportedCode, "unsupported PDF/A conformance"),
        };
    }

    public static bool Requested(IDocumentPreparationRequest input) => input.FlattenForm || input.ConvertToPdfa;

    /// <summary>
    /// Runs the requested steps over a document that passed the structural input check (active
    /// content not checked when flattening is asked for). The result always passed the full
    /// <see cref="PdfSafetyGuard"/> check.
    /// </summary>
    /// <param name="context">Route name for the guard's messages ("Presign", "Timestamp", "Visual sign").</param>
    public static Result Prepare(byte[] pdf, IDocumentPreparationRequest input, LimitsOptions limits, PdfSafetyGuard guard,
        PdfConversionService conversion, string context = "Presign")
    {
        var target = input.ConvertToPdfa ? ResolveConformance(input.PdfaConformance) : default;
        var (signed, declared, embeddedFiles) = Inspect(pdf);

        string? flatten = null;
        var flattenedFields = 0;
        IReadOnlyList<string> removed = Array.Empty<string>();
        if (input.FlattenForm)
        {
            if (signed)
            {
                // The earlier signer's presign flattened it; a rewrite now would break that signature.
                flatten = FlattenOutcome.SkippedSigned;
            }
            else
            {
                var flattened = PdfFormFlattener.FlattenVerified(pdf, limits, guard, context);
                pdf = flattened.Pdf;
                flatten = flattened.Report.Changed ? FlattenOutcome.Flattened : FlattenOutcome.Unchanged;
                flattenedFields = flattened.Report.FlattenedFields;
                removed = flattened.Report.Removed;
            }
        }

        string? pdfa = null;
        string? conformanceName = null;
        PdfAConformance? keep = null;
        if (input.ConvertToPdfa)
        {
            if (signed)
            {
                // Conversion rewrites the file; only a document that is already PDF/A can be signed on.
                if (declared == null) throw new ApiValidationException(PdfFormFlattener.AlreadySignedCode, "signed document is not PDF/A");
                pdfa = PdfaOutcome.AlreadyPdfa;
                conformanceName = $"PDF/A-{declared.GetPart()}{declared.GetLevel()}";
                keep = declared;
            }
            else
            {
                // PDF/A-3 carries embedded files over as associated files (the evidence of a seal, an
                // attachment); PDF/A-2 allows only PDF/A files inside and the conversion would drop them.
                if (embeddedFiles && target.Conformance.GetPart() != "3")
                {
                    throw new ApiValidationException(EmbeddedFilesCode, "document has embedded files");
                }
                pdf = Convert(pdf, target.Name, target.Conformance, conversion);
                pdfa = PdfaOutcome.Converted;
                conformanceName = target.Name;
                keep = target.Conformance;
            }
        }

        var pages = guard.EnsureSafePdf(pdf, context + " result");
        return new Result(pdf, pages, new Report(flatten, flattenedFields, removed, pdfa, conformanceName), keep);
    }

    /// <summary>
    /// Writes the signature into a document that must stay PDF/A: iText checks what it writes into a
    /// PDF/A document and its refusal is <see cref="NotPreservedCode"/>. Without <paramref name="keep"/>
    /// the write runs as it always did.
    /// </summary>
    public static T KeepPdfA<T>(PdfAConformance? keep, Func<T> write)
    {
        try
        {
            return write();
        }
        catch (PdfAConformanceException ex) when (keep != null)
        {
            throw new ApiValidationException(NotPreservedCode, ex.Message);
        }
    }

    /// <summary>The signed or presigned document still meets <paramref name="keep"/>; otherwise <see cref="NotPreservedCode"/>.</summary>
    public static void EnsurePreserved(byte[] pdf, PdfAConformance? keep)
    {
        if (keep == null) return;
        // Content drawn outside iText's PDF/A checks (QR page, stamped fields, a visual signature).
        var problems = PdfAConformanceCheck.Problems(pdf, keep);
        if (problems.Count > 0)
        {
            throw new ApiValidationException(NotPreservedCode, string.Join(",", problems));
        }
    }

    /// <summary>
    /// The appearance for a signature in a PDF/A document: without a family iText draws in plain
    /// Helvetica, which is never embedded, so the embedded Liberation Sans (Helvetica's metrics) is
    /// used. Any other document, or a family given, gets <paramref name="appearance"/> unchanged.
    /// </summary>
    public static SignatureAppearanceOptions? AppearanceFor(PdfDocument document, SignatureAppearanceOptions? appearance)
    {
        if (!document.GetConformance().IsPdfA() || !string.IsNullOrWhiteSpace(appearance?.FontFamily)) return appearance;
        var s = appearance ?? new SignatureAppearanceOptions();
        // A copy: the caller's options stay untouched (they are part of the operation's fingerprint).
        return new SignatureAppearanceOptions
        {
            DescriptionText = s.DescriptionText,
            ForegroundColor = s.ForegroundColor,
            BackgroundColor = s.BackgroundColor,
            FontFamily = "Helvetica",
            FontSize = s.FontSize,
            ShowReason = s.ShowReason,
            ShowLocation = s.ShowLocation,
            ShowDate = s.ShowDate,
            ShowSignerName = s.ShowSignerName,
            ShowCompanyName = s.ShowCompanyName,
            BackgroundRepeat = s.BackgroundRepeat,
            AutoFontSize = s.AutoFontSize,
            Labels = s.Labels,
        };
    }

    private static byte[] Convert(byte[] pdf, string name, PdfAConformance conformance, PdfConversionService conversion)
    {
        byte[] converted;
        try
        {
            converted = System.Convert.FromBase64String(conversion.ConvertToPdfA(new ConvertToPdfAInput { Conformance = name }, pdf));
        }
        catch (PdfAConformanceException ex)
        {
            throw new ApiValidationException(ConversionFailedCode, ex.Message);
        }
        catch (PdfException ex)
        {
            throw new ApiValidationException(ConversionFailedCode, ex.Message);
        }

        var problems = PdfAConformanceCheck.Problems(converted, conformance);
        if (problems.Count > 0)
        {
            throw new ApiValidationException(ConversionFailedCode, string.Join(",", problems));
        }
        return converted;
    }

    /// <summary>Signed?, the declared PDF/A (null when none) and whether there are embedded files, from one parse.</summary>
    private static (bool Signed, PdfAConformance? Declared, bool EmbeddedFiles) Inspect(byte[] pdf)
    {
        PdfOpenCounter.Record();
        using var document = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var names = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
        var embedded = names?.GetAsDictionary(PdfName.EmbeddedFiles) != null
            && document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles).GetNames().Count > 0;
        return (PdfFormFlattener.CarriesSignature(document), document.GetConformance().GetAConformance(), embedded);
    }
}
