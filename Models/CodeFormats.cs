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
/// digit built into the symbology.</param>
/// <param name="RequireCheckDigitWhenUnnamed">The symbology has an OPTIONAL check
/// digit, and a scan that was not asked for this format demands it. Code 39 is the
/// case: requiring its modulo-43 digit turns a one-in-one chance that a stray bar
/// pattern is reported as a value into roughly one in forty-three. The cost is that
/// a real Code 39 printed without a check digit — the majority, since the standard
/// makes it optional — is then only found when the caller names the format.</param>
/// <param name="MinLengthWhenUnnamed">Shortest payload a scan that was not asked for
/// this format will report. Noise decodes short; a real part number, shipping code or
/// membership number does not. 0 means no floor. It is a filter on likelihood, not a
/// proof — a wide enough run of regular rules can still decode to something long
/// enough, which is why naming the format is what turns the guards off, not on.</param>
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
    BarcodeFormat? ReadFormat,
    bool RequireCheckDigitWhenUnnamed = false,
    int MinLengthWhenUnnamed = 0
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
        // Code 93 carries TWO mandatory check characters (C and K) and the decoder
        // verifies both on every read, so it belongs up here with the rest. It was
        // filed below with the unchecked linear formats, which was simply wrong —
        // the optional check digit is Code 39's, not this one's.
        new("code93", new[] { "code-93" }, CodeDimension.OneD, false, true, true, 40, BarcodeFormat.CODE_93),

        // ── 1D, no mandatory check digit ──────────────────────────────────────
        // A run of regular rules can decode to a value that is not on the page, so
        // an unnamed scan looks for these only under a guard. Naming the format is
        // what turns the guard off — that is the caller saying the page holds them.
        new("code39", new[] { "code-39" }, CodeDimension.OneD, true, true, false, 40, BarcodeFormat.CODE_39,
            RequireCheckDigitWhenUnnamed: true),
        // No check character exists in ITF itself. ITF-14 is the dominant real use
        // and carries fourteen digits; six- and eight-digit variants exist but are
        // rare, and short is exactly what noise decodes to.
        new("itf", new[] { "interleaved2of5", "i2of5" }, CodeDimension.OneD, true, true, false, 30, BarcodeFormat.ITF,
            MinLengthWhenUnnamed: 8),
        // Codabar has no checksum in the standard at all — the weakest of the set,
        // and a length floor is the only guard available. Real payloads (library
        // cards, blood-bank labels) run to eight digits or more.
        new("codabar", new[] { "coda-bar" }, CodeDimension.OneD, true, true, false, 30, BarcodeFormat.CODABAR,
            MinLengthWhenUnnamed: 8),
    };

    /// <summary>Everything the engine can stamp into a document.</summary>
    public static IEnumerable<CodeFormatSpec> Writable => All.Where(f => f.CanWrite);

    /// <summary>Everything the engine can find in a document.</summary>
    public static IEnumerable<CodeFormatSpec> Readable => All.Where(f => f.CanRead);

    /// <summary>
    /// What a scan looks for when the caller did not name a symbology.
    ///
    /// Everything readable — but the formats that are not self-checking come with
    /// their guards on (see <see cref="IsPlausibleWhenUnnamed"/> and
    /// <see cref="RequiresCode39CheckDigit"/>). Leaving them out entirely, which is
    /// where this started, meant a page really carrying a Code 39 came back empty;
    /// letting them in bare meant a table rule was reported as a value that is not
    /// on the page. The guards are the middle: found when the evidence is there,
    /// silent when it is not.
    /// </summary>
    public static IEnumerable<CodeFormatSpec> ScanDefault => Readable;

    /// <summary>
    /// Is this decoded value worth reporting, given nobody asked for this format?
    ///
    /// Only the length floor lives here; the Code 39 check digit is enforced inside
    /// the decoder, which is stronger because it rejects the read rather than the
    /// result. Codabar's start and stop characters are not payload and do not count
    /// towards the floor — the decoder is configured to return them.
    /// </summary>
    public static bool IsPlausibleWhenUnnamed(CodeFormatSpec spec, string? text)
    {
        if (spec.MinLengthWhenUnnamed <= 0) return true;
        var value = (text ?? "").Trim();
        if (spec.ReadFormat == BarcodeFormat.CODABAR && value.Length >= 2)
        {
            value = value.Substring(1, value.Length - 2);
        }
        return value.Length >= spec.MinLengthWhenUnnamed;
    }

    /// <summary>
    /// Whether this scan should demand Code 39's optional modulo-43 check digit.
    ///
    /// True whenever Code 39 is being looked for without having been named. It is a
    /// decoder setting rather than a filter, so a bar pattern that fails it is never
    /// a result at all — roughly one stray pattern in forty-three still gets through,
    /// against all of them before.
    /// </summary>
    public static bool RequiresCode39CheckDigit(IEnumerable<CodeFormatSpec> specs, bool guarded) =>
        guarded && specs.Any(f => f.ReadFormat == BarcodeFormat.CODE_39 && f.RequireCheckDigitWhenUnnamed);

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
    /// them. A group is still a guarded scan: "look for barcodes" is not the same
    /// claim as "this page holds a Code 39 without a check digit". Only naming one
    /// format is specific enough to turn the guards off.
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
