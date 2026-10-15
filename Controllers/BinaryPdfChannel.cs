using System.Text.Json;
using DotNetSigningServer.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DotNetSigningServer.Controllers;

/// <summary>
/// Interní binární kanál portál (v0) → dotnet (#74, rozhodnutí 15. 10. 2026).
///
/// Tělo je <c>multipart/form-data</c> o přesně dvou částech: <c>metadata</c> (JSON stejného tvaru
/// jako dnešní tělo endpointu, jen bez <c>pdfContent</c>) a <c>pdf</c> (bajty dokumentu,
/// <c>application/pdf</c>). PDF se čte rovnou do pole bajtů — žádný base64 řetězec, žádný UTF-16
/// mezikrok — a velikost se hlídá už při čtení, takže soubor nad limitem se do paměti celý
/// nedostane.
///
/// Veřejné API (SPFx, konektor) to není: v OpenAPI ani v <c>ApiDocsCatalog</c> není a base64
/// v JSONu zůstává. Popis kanálu je v <c>docs/binary-channel.md</c>.
/// </summary>
public static class BinaryPdfChannel
{
    public const string ContentType = "multipart/form-data";
    public const string PdfPart = "pdf";
    public const string MetadataPart = "metadata";

    /// <summary>Tělo kanálu není, jak má být: chybí část, je navíc, metadata nejsou JSON…</summary>
    public const string InvalidCode = "MULTIPART_INVALID";

    /// <summary>
    /// Přečte obě části. Chyby jsou <see cref="ApiValidationException"/>:
    /// <see cref="InvalidCode"/> pro tvar těla (včetně metadat nad <paramref name="maxMetadataBytes"/>
    /// a useknutého nebo nečitelného těla), <c>PDF_SIZE_EXCEEDED</c> pro PDF nad
    /// <paramref name="maxPdfBytes"/> (stejný kód jako v JSON kanálu). Tělo nad limitem serveru
    /// (<c>BadHttpRequestException</c> 413) projde ven beze změny.
    /// </summary>
    /// <param name="sizeHint">Délka celého těla, je-li známá — jen HORNÍ odhad velikosti části
    /// <c>pdf</c>: buffer roste od malého zdvojováním a nad tuto hodnotu se nezvětší dřív, než
    /// je potřeba. Podle hlavičky volajícího se předem nic velkého nealokuje.</param>
    /// <param name="maxBodyBytes">Strop celého těla, hlídaný i tady, nejen serverem: tělo bez
    /// Content-Length (chunked) jinak zastaví jen <c>IHttpMaxRequestBodySizeFeature</c>, a to
    /// nemá každý hostitel. Překročení = <c>BadHttpRequestException</c> 413, jako od Kestrelu.</param>
    public static async Task<(T Metadata, byte[] Pdf)> ReadAsync<T>(
        string? contentType,
        Stream body,
        long? sizeHint,
        JsonSerializerOptions json,
        long maxPdfBytes,
        long maxMetadataBytes,
        CancellationToken ct,
        long? maxBodyBytes = null) where T : class
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("not multipart/form-data");
        }
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70)
        {
            throw Invalid("missing or overlong boundary");
        }

        if (maxBodyBytes is > 0) body = new LimitedReadStream(body, maxBodyBytes.Value);
        var reader = new MultipartReader(boundary, body)
        {
            // Preambule a každá část nejvýš tolik; vlastní limity částí níž zaberou dřív
            // (proto rezerva), tohle hlídá preambuli, kterou by čtečka jinak četla bez konce.
            BodyLengthLimit = Math.Max(maxPdfBytes, maxMetadataBytes) + 64 * 1024,
        };
        T? metadata = null;
        byte[]? pdf = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid("part without form-data disposition");
                }
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (string.Equals(name, MetadataPart, StringComparison.Ordinal))
                {
                    if (metadata != null) throw Invalid("metadata twice");
                    // Omezené PŘED deserializací: JSON nad limitem se do paměti celý nedostane.
                    var raw = await ReadBoundedAsync(section.Body, null, maxMetadataBytes, ct,
                        () => Invalid($"metadata over {maxMetadataBytes} bytes"));
                    try
                    {
                        metadata = JsonSerializer.Deserialize<T>(raw, json);
                    }
                    catch (JsonException)
                    {
                        throw Invalid("metadata is not JSON of the expected shape");
                    }
                    if (metadata == null) throw Invalid("metadata is null");
                }
                else if (string.Equals(name, PdfPart, StringComparison.Ordinal))
                {
                    if (pdf != null) throw Invalid("pdf twice");
                    if (!IsPdfMediaType(section.ContentType)) throw Invalid("pdf part is not application/pdf");
                    pdf = await ReadBoundedAsync(section.Body, sizeHint, maxPdfBytes, ct,
                        () => new ApiValidationException("PDF_SIZE_EXCEEDED", $"pdf part over {maxPdfBytes} bytes"));
                }
                else
                {
                    // Přísně: neznámá část je chyba volajícího, ne něco, co se tiše zahodí.
                    throw Invalid("unknown part");
                }
            }
        }
        catch (InvalidDataException)
        {
            // Rozbité hranice nebo hlavičky části.
            throw Invalid("malformed multipart body");
        }
        catch (IOException ex) when (ex is not Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            // Useknuté tělo, spojení, které skončilo uprostřed: obojí je tělo, které nejde přečíst.
            // Tělo nad limitem serveru (BadHttpRequestException, 413) jde ven, ať zůstane 413.
            throw Invalid("truncated or unreadable multipart body");
        }

        if (metadata == null || pdf == null) throw Invalid("metadata or pdf part missing");
        return (metadata, pdf);
    }

    /// <summary>Chybějící Content-Type části se bere jako PDF; jiný než PDF/octet-stream ne.</summary>
    private static bool IsPdfMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!MediaTypeHeaderValue.TryParse(value, out var type)) return false;
        return type.MediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || type.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Počáteční buffer: malý, ať odhad velikosti od volajícího nic předem nealokuje.</summary>
    internal const int InitialBufferBytes = 256 * 1024;

    /// <summary>
    /// Přečte část do pole přesné délky. Buffer začíná na <see cref="InitialBufferBytes"/>
    /// a zdvojuje se; <paramref name="sizeHint"/> růst jen shora omezuje, dokud stačí. Nad
    /// <paramref name="max"/> skončí hned (<paramref name="tooLarge"/>), ne až po přečtení celé části.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(
        Stream source, long? sizeHint, long max, CancellationToken ct, Func<ApiValidationException>? tooLarge = null)
    {
        tooLarge ??= () => new ApiValidationException("PDF_SIZE_EXCEEDED", $"part over {max} bytes");
        var cap = (int)Math.Min(max, Array.MaxLength - 1) + 1; // o bajt víc: tak se pozná „víc, než smí“
        var buffer = new byte[Math.Min(InitialBufferBytes, cap)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= cap) throw tooLarge();
                long grown = Math.Min((long)buffer.Length * 2, cap);
                // Část nemůže být delší než celé tělo: dokud odhad stačí, nad něj nerůst.
                if (sizeHint is > 0 && buffer.Length < sizeHint.Value) grown = Math.Min(grown, sizeHint.Value);
                Array.Resize(ref buffer, (int)grown);
            }
            var read = await source.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (length >= cap) throw tooLarge();
        }
        if (length != buffer.Length) Array.Resize(ref buffer, length);
        return buffer;
    }

    /// <summary>Čte nejvýš <c>limit</c> bajtů; dál hodí 413 jako Kestrel.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        private int Count(int n)
        {
            _read += n;
            if (_read > limit)
            {
                throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                    $"Request body over {limit} bytes", Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static ApiValidationException Invalid(string detail) => new(InvalidCode, detail);
}
