using System.Security.Cryptography;
using System.Text;
using iText.IO.Source;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services;

/// <summary>The validation data (DSS) of a PDF as it stood at the end of one revision.</summary>
internal sealed class DssSnapshot
{
    private static readonly PdfName DssName = new("DSS");
    private static readonly PdfName VriName = new("VRI");
    private static readonly PdfName CertsName = new("Certs");
    private static readonly PdfName OcspsName = new("OCSPs");
    private static readonly PdfName CrlsName = new("CRLs");
    private static readonly PdfName VriOcspName = new("OCSP");
    private static readonly PdfName VriCrlName = new("CRL");

    /// <summary>A revision that was not read (budget spent, no readable trailer). Covers nothing.</summary>
    public static readonly DssSnapshot Unknown = new(isUnknown: true);

    private DssSnapshot(bool isUnknown) => IsUnknown = isUnknown;

    public bool IsUnknown { get; }
    public bool HasVri { get; private init; }
    /// <summary>VRI key → whether the entry lists at least one OCSP response or CRL.</summary>
    public Dictionary<string, bool> Vri { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 (hex) of every certificate in /Certs.</summary>
    public HashSet<string> Certificates { get; } = new(StringComparer.Ordinal);
    /// <summary>SHA-256 (hex) of every OCSP response and CRL in /OCSPs and /CRLs.</summary>
    public HashSet<string> Revocation { get; } = new(StringComparer.Ordinal);

    /// <summary>The DSS of an open document, or null when it has none.</summary>
    public static DssSnapshot? From(PdfDocument document)
    {
        var dss = document.GetCatalog().GetPdfObject().GetAsDictionary(DssName);
        if (dss == null) return null;

        var vri = dss.GetAsDictionary(VriName);
        var snapshot = new DssSnapshot(isUnknown: false) { HasVri = vri != null };
        if (vri != null)
        {
            foreach (var key in vri.KeySet())
            {
                var entry = vri.GetAsDictionary(key);
                var hasRevocation = (entry?.GetAsArray(VriOcspName)?.Size() ?? 0) > 0
                    || (entry?.GetAsArray(VriCrlName)?.Size() ?? 0) > 0;
                snapshot.Vri[key.GetValue()] = hasRevocation;
            }
        }

        AddStreamHashes(dss.GetAsArray(CertsName), snapshot.Certificates);
        AddStreamHashes(dss.GetAsArray(OcspsName), snapshot.Revocation);
        AddStreamHashes(dss.GetAsArray(CrlsName), snapshot.Revocation);
        return snapshot;
    }

    private static void AddStreamHashes(PdfArray? array, HashSet<string> into)
    {
        if (array == null) return;
        for (var i = 0; i < array.Size(); i++)
        {
            var bytes = array.GetAsStream(i)?.GetBytes();
            if (bytes != null) into.Add(Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
}

/// <summary>
/// Reads the DSS as it stood at the end of a given revision, cheaply enough to
/// run on an untrusted upload.
///
/// A revision is only opened when its own new bytes could have touched the DSS
/// (they mention <c>/DSS</c>, <c>/VRI</c>, <c>/Certs</c>, <c>/OCSPs</c>,
/// <c>/CRLs</c> or hold an object stream); otherwise it inherits the snapshot of
/// the revision before. A prefix is only parsed when its <c>startxref</c> points
/// at an xref table or xref stream, so a forged trailer cannot make iText rebuild
/// the cross-reference table for every prefix. All opened prefixes together may
/// not exceed <see cref="BudgetFactor"/> × the file size; past that a revision is
/// <see cref="DssSnapshot.Unknown"/>, which covers nothing.
/// </summary>
internal sealed class PdfValidationDataIndex
{
    internal const int BudgetFactor = 3;

    private static readonly byte[][] Markers =
        new[] { "/DSS", "/VRI", "/Certs", "/OCSPs", "/CRLs", "/ObjStm" }
            .Select(Encoding.ASCII.GetBytes).ToArray();

    private readonly byte[] _pdf;
    private readonly IReadOnlyList<long> _ends;
    private readonly bool[] _mayChange;
    private readonly long _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<int, DssSnapshot?> _cache = new();

    public PdfValidationDataIndex(byte[] pdf, IReadOnlyList<long> revisionEnds, DssSnapshot? final, CancellationToken cancellationToken)
    {
        _pdf = pdf;
        _ends = revisionEnds;
        _budget = (long)BudgetFactor * pdf.LongLength;
        _cancellationToken = cancellationToken;
        Final = final;

        _mayChange = new bool[revisionEnds.Count];
        long start = 0;
        for (var i = 0; i < revisionEnds.Count; i++)
        {
            _mayChange[i] = MentionsValidationData(pdf.AsSpan((int)start, (int)(revisionEnds[i] - start)));
            start = revisionEnds[i];
        }
    }

    private static bool MentionsValidationData(ReadOnlySpan<byte> segment)
    {
        foreach (var marker in Markers)
        {
            if (segment.IndexOf(marker) >= 0) return true;
        }
        return false;
    }

    /// <summary>The DSS of the whole file as received.</summary>
    public DssSnapshot? Final { get; }

    /// <summary>Bytes of all prefixes parsed so far.</summary>
    public long OpenedBytes { get; private set; }

    /// <summary>
    /// The DSS at the end of revision <paramref name="index"/> (0-based).
    /// <paramref name="read"/> opens that revision even when its bytes do not
    /// look like they touch the DSS (used for what a document timestamp covers).
    /// </summary>
    public DssSnapshot? At(int index, bool read = false)
    {
        var source = index;
        if (!read)
        {
            while (source >= 0 && !_mayChange[source]) source--;
            if (source < 0) return null;
        }

        if (_cache.TryGetValue(source, out var cached)) return cached;
        _cancellationToken.ThrowIfCancellationRequested();

        var snapshot = Read(_ends[source]);
        _cache[source] = snapshot;
        return snapshot;
    }

    private DssSnapshot? Read(long end)
    {
        if (end >= _pdf.LongLength) return Final;
        if (OpenedBytes + end > _budget) return DssSnapshot.Unknown;
        if (!PdfRevisionScanner.EndsWithTrailer(_pdf, end)) return DssSnapshot.Unknown;

        OpenedBytes += end;
        try
        {
            // A window over the same array, not a copy of the file per revision.
            // A fresh source each time: closing the reader closes its source.
            var file = new RandomAccessSourceFactory().CreateSource(_pdf);
            using var reader = new PdfReader(new WindowRandomAccessSource(file, 0, end), new ReaderProperties());
            using var document = new PdfDocument(reader);
            return DssSnapshot.From(document);
        }
        catch (Exception)
        {
            // A prefix that does not open as a PDF is not a revision we can read.
            return DssSnapshot.Unknown;
        }
    }
}
