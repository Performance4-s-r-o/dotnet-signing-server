using ZXing;

namespace DotNetSigningServer.Models;

/// <summary>Linear (1D) or matrix (2D) symbology.</summary>
public enum CodeDimension
{
    OneD,
    TwoD
}

/// <summary>
/// One symbology, and everything the rest of the system needs to know about it.
/// </summary>
/// <param name="Id">Canonical name on the wire. The same string in the engine, the
/// portal, the SharePoint extension and the Power Automate connector.</param>
/// <param name="Aliases">Other spellings accepted on input. Existing templates were
/// written with these, so they must keep resolving.</param>
/// <param name="CanWrite">The engine can stamp this into a document.</param>
/// <param name="CanRead">The engine can find this in a document.</param>
/// <param name="SelfChecking">A misread is rejected by the decoder rather than
/// returned as a value — either through error correction (2D) or a mandatory check
/// digit. Decides whether the format joins a scan that was not asked to look for
/// anything in particular.</param>
/// <param name="MaxLength">How much text is worth putting in. Not a hard limit of the
/// symbology; a readability cap, enforced by the fill UI.</param>
/// <param name="ReadFormat">The ZXing format, when it can be read.</param>
public sealed record CodeFormatSpec(
    string Id,
    string[] Aliases,
    CodeDimension Dimension,
    bool CanWrite,
    bool CanRead,
    bool SelfChecking,
    int MaxLength,
    BarcodeFormat? ReadFormat
);

/// <summary>
/// The one list of symbologies this system knows.
///
/// It exists because there were three lists and they disagreed. The engine could
/// WRITE nine formats but only ever looked for four when reading, so a Code 128 it
/// had stamped itself came back as "no codes found". The SharePoint template builder
/// offered three choices — QR, Data Matrix, "barcode" — and collapsed everything else
/// to Code 128 on save, so opening an EAN-13 template and saving it silently changed
/// the symbology. Nothing anywhere said which names were legal.
///
/// So: one list, and both directions derive from it. `ParseFormats` reads it,
/// `TryAddBarcode` reads it, and `GET /api/code-formats` publishes it so the portal
/// and the extension can offer exactly what the engine supports instead of guessing.
///
/// **Two formats are read-only** (`aztec`, `code93`): iText ships no generator for
/// them. Dropping them from reading to make the two directions match would lose the
/// ability to find a code in a document somebody else produced, which is the more
/// useful half. They are marked, not hidden.
///
/// **MSI, Plessey, RSS/DataBar, MaxiCode and PostNet are deliberately absent.** ZXing
/// can decode several of them and iText can write MSI and PostNet, but they carry no
/// usable check digit and decode noise readily; adding them would cost accuracy on
/// every scan to serve symbologies nobody has asked for.
/// </summary>
public static class CodeFormats
{
    public static readonly IReadOnlyList<CodeFormatSpec> All = new List<CodeFormatSpec>
    {
        // ── 2D ────────────────────────────────────────────────────────────────
        new("qr", new[] { "qrcode", "qr-code" }, CodeDimension.TwoD, true, true, true, 256, BarcodeFormat.QR_CODE),
        new("datamatrix", new[] { "data-matrix", "dm" }, CodeDimension.TwoD, true, true, true, 100, BarcodeFormat.DATA_MATRIX),
        new("pdf417", new[] { "pdf-417" }, CodeDimension.TwoD, true, true, true, 256, BarcodeFormat.PDF_417),
        // No iText generator. Readable so a document from outside can still be scanned.
        new("aztec", Array.Empty<string>(), CodeDimension.TwoD, false, true, true, 256, BarcodeFormat.AZTEC),

        // ── 1D, self-checking ─────────────────────────────────────────────────
        new("code128", new[] { "code-128" }, CodeDimension.OneD, true, true, true, 40, BarcodeFormat.CODE_128),
        new("ean13", new[] { "ean-13" }, CodeDimension.OneD, true, true, true, 13, BarcodeFormat.EAN_13),
        new("ean8", new[] { "ean-8" }, CodeDimension.OneD, true, true, true, 8, BarcodeFormat.EAN_8),
        new("upca", new[] { "upc", "upc-a" }, CodeDimension.OneD, true, true, true, 12, BarcodeFormat.UPC_A),
        new("upce", new[] { "upc-e" }, CodeDimension.OneD, true, true, true, 8, BarcodeFormat.UPC_E),

        // ── 1D, no mandatory check digit ──────────────────────────────────────
        // Any run of bars can decode to something, so these stay out of a scan
        // that was not asked to look for them. See ScanDefault.
        new("code39", new[] { "code-39" }, CodeDimension.OneD, true, true, false, 40, BarcodeFormat.CODE_39),
        // No iText generator.
        new("code93", new[] { "code-93" }, CodeDimension.OneD, false, true, false, 40, BarcodeFormat.CODE_93),
        new("itf", new[] { "interleaved2of5", "i2of5" }, CodeDimension.OneD, true, true, false, 30, BarcodeFormat.ITF),
        new("codabar", new[] { "coda-bar" }, CodeDimension.OneD, true, true, false, 30, BarcodeFormat.CODABAR),
    };

    /// <summary>Everything the engine can stamp into a document.</summary>
    public static IEnumerable<CodeFormatSpec> Writable => All.Where(f => f.CanWrite);

    /// <summary>Everything the engine can find in a document.</summary>
    public static IEnumerable<CodeFormatSpec> Readable => All.Where(f => f.CanRead);

    /// <summary>
    /// What a scan looks for when the caller did not name a symbology.
    ///
    /// Self-checking formats only. On a 300 DPI render of an ordinary page, a table
    /// rule or an underline decodes as a Code 39 or an ITF value that is not there,
    /// and a confident wrong answer is worse than a missing one. Asking for those by
    /// name is the caller saying the page really does hold them.
    /// </summary>
    public static IEnumerable<CodeFormatSpec> ScanDefault => Readable.Where(f => f.SelfChecking);

    /// <summary>Resolve a name or alias. Case- and spelling-tolerant, never guesses.</summary>
    public static CodeFormatSpec? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().ToLowerInvariant();
        return All.FirstOrDefault(f =>
            f.Id == normalized || f.Aliases.Contains(normalized));
    }

    /// <summary>
    /// A group name, or null.
    ///
    /// `1d`/`2d` are how a caller says "look for the linear ones" without listing
    /// them; `1d` is also the only way to reach the formats that are not
    /// self-checking, which is why it is a deliberate word rather than a default.
    /// </summary>
    public static IEnumerable<CodeFormatSpec>? FindGroup(string? name)
    {
        var normalized = (name ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "1d" or "linear" or "barcode" => Readable.Where(f => f.Dimension == CodeDimension.OneD),
            "2d" or "matrix" => Readable.Where(f => f.Dimension == CodeDimension.TwoD),
            _ => null,
        };
    }
}
