using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Pdfa.Exceptions;

namespace DotNetSigningServer.Services;

/// <summary>
/// What presign does to the document before the signature is prepared (#184), when asked:
/// (1) flattening the form and removing active content — the step of <c>/api/flatten-form</c>,
/// skipped on a signed document; (2) conversion to PDF/A, checked afterwards. Nothing falls back
/// to the unconverted document: a document that cannot be converted is refused.
/// </summary>
public static class PresignPreparation
{
    public const string ConversionFailedCode = "PDFA_CONVERSION_FAILED";
    public const string NotPreservedCode = "PDFA_NOT_PRESERVED";
    public const string ConformanceUnsupportedCode = "PDFA_CONFORMANCE_UNSUPPORTED";

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
    /// <param name="KeepPdfA">Set when the result is PDF/A: the presign must keep it so.</param>
    public sealed record Result(byte[] Pdf, int Pages, Report Report, PdfAConformance? KeepPdfA);

    private const string Context = "Presign";

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

    public static bool Requested(PreSignInput input) => input.FlattenForm || input.ConvertToPdfa;

    /// <summary>
    /// Runs the requested steps over a document that passed the structural input check (active
    /// content not checked when flattening is asked for). The result always passed the full
    /// <see cref="PdfSafetyGuard"/> check.
    /// </summary>
    public static Result Prepare(byte[] pdf, PreSignInput input, LimitsOptions limits, PdfSafetyGuard guard, PdfConversionService conversion)
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
                var flattened = PdfFormFlattener.FlattenVerified(pdf, limits, guard, Context);
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
                // The conversion copies pages only: embedded files would be dropped without a word.
                if (embeddedFiles) throw new ApiValidationException(ConversionFailedCode, "document has embedded files");
                pdf = Convert(pdf, target.Name, target.Conformance, conversion);
                pdfa = PdfaOutcome.Converted;
                conformanceName = target.Name;
                keep = target.Conformance;
            }
        }

        var pages = guard.EnsureSafePdf(pdf, Context + " result");
        return new Result(pdf, pages, new Report(flatten, flattenedFields, removed, pdfa, conformanceName), keep);
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
