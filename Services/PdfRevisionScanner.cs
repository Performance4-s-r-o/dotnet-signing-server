using System.Security.Cryptography;
using System.Text;

namespace DotNetSigningServer.Services;

/// <summary>
/// Finds where each incremental revision of a PDF ends, by bytes alone.
///
/// A revision ends where a signature's ByteRange ends (the signed bytes up to
/// that point) or after a <c>%%EOF</c> marker that closes a real trailer. The
/// second source is a heuristic: a <c>%%EOF</c> can also sit inside a stream,
/// for example in an attached PDF, so a marker only counts when the
/// <c>startxref</c> before it points at an <c>xref</c> table or an xref stream
/// object earlier in this file.
/// </summary>
internal static class PdfRevisionScanner
{
    private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("%%EOF");
    private static readonly byte[] StartXref = Encoding.ASCII.GetBytes("startxref");
    private static readonly byte[] XrefKeyword = Encoding.ASCII.GetBytes("xref");
    private static readonly byte[] XrefType = Encoding.ASCII.GetBytes("/XRef");
    private static readonly byte[] StreamKeyword = Encoding.ASCII.GetBytes("stream");

    /// <summary>
    /// End offsets (exclusive) of every revision, ascending and distinct.
    /// <paramref name="signatureEnds"/> must already be within the file.
    /// </summary>
    internal static List<long> FindRevisionEnds(byte[] pdf, IEnumerable<long> signatureEnds)
    {
        var ends = new SortedSet<long>(signatureEnds.Where(e => e > 0 && e <= pdf.Length));

        var index = 0;
        while ((index = IndexOf(pdf, EofMarker, index)) >= 0)
        {
            if (ClosesATrailer(pdf, index))
            {
                ends.Add(SkipSingleEol(pdf, index + EofMarker.Length));
            }
            index += EofMarker.Length;
        }

        // A file without a recognisable marker is still one revision.
        if (ends.Count == 0) ends.Add(pdf.Length);
        return ends.ToList();
    }

    /// <summary>
    /// True when the bytes [0, end) finish with a <c>%%EOF</c> (and at most one
    /// end of line) that closes a real trailer. Only such a prefix is worth
    /// handing to a PDF parser.
    /// </summary>
    internal static bool EndsWithTrailer(byte[] pdf, long end)
    {
        if (end <= 0 || end > pdf.LongLength) return false;
        var from = (int)Math.Max(0, end - EofMarker.Length - 2);
        var marker = pdf.AsSpan(from, (int)end - from).LastIndexOf(EofMarker);
        if (marker < 0) return false;
        var index = from + marker;
        return SkipSingleEol(pdf, index + EofMarker.Length) == end && ClosesATrailer(pdf, index);
    }

    /// <summary>SHA-256 of the bytes [0, end) for every end, in one pass over the file.</summary>
    internal static List<string> HashPrefixes(byte[] pdf, IReadOnlyList<long> ends)
    {
        var hashes = new List<string>(ends.Count);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long position = 0;
        foreach (var end in ends)
        {
            hash.AppendData(pdf, (int)position, (int)(end - position));
            position = end;
            hashes.Add(Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant());
        }
        return hashes;
    }

    /// <summary>The marker ends the line, so the end of line belongs to the revision.</summary>
    private static long SkipSingleEol(byte[] pdf, int end)
    {
        if (end < pdf.Length && pdf[end] == '\r') end++;
        if (end < pdf.Length && pdf[end] == '\n') end++;
        return end;
    }

    /// <summary>
    /// True when the marker is preceded by <c>startxref &lt;offset&gt;</c> and
    /// that offset lands on an <c>xref</c> table or on an <c>N G obj</c> whose
    /// dictionary is <c>/Type /XRef</c>. Any other object would only make a parser
    /// rebuild the cross-reference table by scanning the whole prefix.
    /// </summary>
    private static bool ClosesATrailer(byte[] pdf, int markerIndex)
    {
        var i = SkipWhitespaceBackwards(pdf, markerIndex - 1);
        var digitsEnd = i;
        while (i >= 0 && IsDigit(pdf[i])) i--;
        if (i == digitsEnd) return false;
        var digitsStart = i + 1;

        var keywordEnd = SkipWhitespaceBackwards(pdf, i);
        if (keywordEnd == i) return false; // no whitespace between keyword and offset
        var keywordStart = keywordEnd - StartXref.Length + 1;
        if (keywordStart < 0 || !Matches(pdf, keywordStart, StartXref)) return false;

        if (digitsEnd - digitsStart + 1 > 18) return false;
        long offset = 0;
        for (var d = digitsStart; d <= digitsEnd; d++) offset = offset * 10 + (pdf[d] - '0');
        if (offset >= keywordStart) return false;

        return Matches(pdf, (int)offset, XrefKeyword) || IsXrefStream(pdf, (int)offset);
    }

    private static bool IsXrefStream(byte[] pdf, int offset)
    {
        var i = offset;
        if (!ReadDigits(pdf, ref i)) return false;
        if (!ReadWhitespace(pdf, ref i)) return false;
        if (!ReadDigits(pdf, ref i)) return false;
        if (!ReadWhitespace(pdf, ref i)) return false;
        if (!(i + 3 <= pdf.Length && pdf[i] == 'o' && pdf[i + 1] == 'b' && pdf[i + 2] == 'j')) return false;

        // The stream dictionary sits right after the header; /Type /XRef must be in it.
        var window = pdf.AsSpan(i + 3, Math.Min(1024, pdf.Length - i - 3));
        var stream = window.IndexOf(StreamKeyword);
        if (stream >= 0) window = window[..stream];
        return window.IndexOf(XrefType) >= 0;
    }

    private static bool ReadDigits(byte[] pdf, ref int i)
    {
        var start = i;
        while (i < pdf.Length && IsDigit(pdf[i])) i++;
        return i > start;
    }

    private static bool ReadWhitespace(byte[] pdf, ref int i)
    {
        var start = i;
        while (i < pdf.Length && IsWhitespace(pdf[i])) i++;
        return i > start;
    }

    private static int SkipWhitespaceBackwards(byte[] pdf, int i)
    {
        while (i >= 0 && IsWhitespace(pdf[i])) i--;
        return i;
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0;

    private static bool Matches(byte[] pdf, int at, byte[] pattern) =>
        at >= 0 && at + pattern.Length <= pdf.Length && pdf.AsSpan(at, pattern.Length).SequenceEqual(pattern);

    private static int IndexOf(byte[] pdf, byte[] pattern, int from)
    {
        if (from >= pdf.Length) return -1;
        var found = pdf.AsSpan(from).IndexOf(pattern);
        return found < 0 ? -1 : from + found;
    }
}
