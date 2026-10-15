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
    /// <see cref="InvalidCode"/> pro tvar těla, <c>PDF_SIZE_EXCEEDED</c> pro PDF nad
    /// <paramref name="maxPdfBytes"/> (stejný kód jako v JSON kanálu).
    /// </summary>
    /// <param name="sizeHint">Délka celého těla, je-li známá: PDF je jeho většina, takže buffer
    /// rovnou té velikosti se nemusí cestou zvětšovat.</param>
    public static async Task<(T Metadata, byte[] Pdf)> ReadAsync<T>(
        string? contentType,
        Stream body,
        long? sizeHint,
        JsonSerializerOptions json,
        long maxPdfBytes,
        CancellationToken ct) where T : class
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

        var reader = new MultipartReader(boundary, body);
        T? metadata = null;
        byte[]? pdf = null;
        MultipartSection? section;
        while ((section = await ReadSectionAsync(reader, ct)) != null)
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
                try
                {
                    metadata = await JsonSerializer.DeserializeAsync<T>(section.Body, json, ct);
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
                pdf = await ReadBoundedAsync(section.Body, sizeHint, maxPdfBytes, ct);
            }
            else
            {
                // Přísně: neznámá část je chyba volajícího, ne něco, co se tiše zahodí.
                throw Invalid("unknown part");
            }
        }

        if (metadata == null || pdf == null) throw Invalid("metadata or pdf part missing");
        return (metadata, pdf);
    }

    private static async Task<MultipartSection?> ReadSectionAsync(MultipartReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadNextSectionAsync(ct);
        }
        catch (InvalidDataException)
        {
            // Rozbité hranice nebo hlavičky části.
            throw Invalid("malformed multipart body");
        }
        catch (IOException ex) when (ex.InnerException == null && ex.Message.Contains("Unexpected end", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("truncated multipart body");
        }
    }

    /// <summary>Chybějící Content-Type části se bere jako PDF; jiný než PDF/octet-stream ne.</summary>
    private static bool IsPdfMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!MediaTypeHeaderValue.TryParse(value, out var type)) return false;
        return type.MediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || type.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Přečte část do pole přesné délky. Nad limitem skončí hned, ne až po přečtení celé části.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream source, long? sizeHint, long max, CancellationToken ct)
    {
        var limit = (int)Math.Min(max, Array.MaxLength - 1);
        var initial = (int)Math.Clamp(sizeHint ?? 64 * 1024, 4 * 1024, (long)limit + 1);
        var buffer = new byte[initial];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                // Plný buffer ještě nemusí znamenat konec: zvětšit nejvýš o jeden bajt nad limit,
                // aby šlo poznat „víc, než smí“.
                var grown = (int)Math.Min((long)buffer.Length * 2, (long)limit + 1);
                if (grown <= buffer.Length) throw new ApiValidationException("PDF_SIZE_EXCEEDED", $"pdf part over {max} bytes");
                Array.Resize(ref buffer, grown);
            }
            var read = await source.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (length > limit) throw new ApiValidationException("PDF_SIZE_EXCEEDED", $"pdf part over {max} bytes");
        }
        if (length != buffer.Length) Array.Resize(ref buffer, length);
        return buffer;
    }

    private static ApiValidationException Invalid(string detail) => new(InvalidCode, detail);
}
