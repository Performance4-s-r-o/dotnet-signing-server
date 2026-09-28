using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Documents;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Stores the last successful answer of the service in <c>LegalDocuments</c>, keyed by the
/// existing unique index (Slug, Locale, Version), so it can be served while the service is
/// unreachable.
///
/// A new row gets <c>Source = backoffice</c>, <c>Content = ""</c> and <c>IsDraft = false</c>.
/// A hand-maintained row of the same version is never taken over: only the snapshot columns
/// (<c>ContentHtml</c>, <c>ContentHash</c>, <c>ChangeKind</c>, <c>TypeKey</c>,
/// <c>FetchedAt</c>) are filled in, so switching the module Off still shows exactly the
/// hand-written Markdown, title and date.
/// </summary>
public sealed partial class LegalDocumentsSnapshotWriter
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<LegalDocumentsSnapshotWriter> _logger;

    public LegalDocumentsSnapshotWriter(ApplicationDbContext db, TimeProvider time, ILogger<LegalDocumentsSnapshotWriter> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Inserts or updates the snapshot row of <paramref name="document"/> (in the locale the
    /// service actually served). Returns true when a row was written, false when it was
    /// already up to date or the document is not one this product shows. Throws on a
    /// database error.
    /// </summary>
    public async Task<bool> UpsertAsync(BackofficeDocumentContent document, CancellationToken cancellationToken = default)
    {
        var slug = LegalSlugMap.SlugFor(document.Type);
        var locale = document.Locale.Trim().ToLowerInvariant();
        if (slug is null || locale.Length is 0 or > 8 || document.Version <= 0 || string.IsNullOrWhiteSpace(document.Html))
        {
            return false;
        }

        try
        {
            return await UpsertOnceAsync(document, slug, locale, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another instance inserted the same (Slug, Locale, Version) first; update theirs.
            _logger.LogDebug(ex, "[legal-docs] snapshot insert raced for {Slug}/{Locale} v{Version}; retrying", slug, locale, document.Version);
            _db.ChangeTracker.Clear();
            return await UpsertOnceAsync(document, slug, locale, cancellationToken);
        }
    }

    private async Task<bool> UpsertOnceAsync(BackofficeDocumentContent document, string slug, string locale, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var hash = NormaliseHash(document.ContentHash);
        var row = await _db.LegalDocuments.FirstOrDefaultAsync(
            d => d.Slug == slug && d.Locale == locale && d.Version == document.Version, cancellationToken);

        if (row is null)
        {
            _db.LegalDocuments.Add(new LegalDocument
            {
                Slug = slug,
                Locale = locale,
                Version = document.Version,
                Title = Truncate(string.IsNullOrWhiteSpace(document.Title) ? document.Name : document.Title, 256),
                Summary = document.Summary is null ? null : Truncate(document.Summary, 1024),
                Content = "",
                EffectiveFrom = document.EffectiveFrom ?? now,
                IsDraft = false,
                CreatedAt = now,
                UpdatedAt = now,
                TypeKey = document.Type,
                ContentHtml = document.Html,
                ContentHash = hash,
                ChangeKind = TruncateOrNull(document.ChangeKind, 16),
                Source = LegalDocumentSources.Backoffice,
                FetchedAt = now,
            });
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (row.ContentHtml == document.Html && row.ContentHash == hash)
        {
            return false;
        }

        if (row.Source == LegalDocumentSources.Backoffice)
        {
            // A minor correction keeps the version and changes the text (and possibly the title).
            row.Title = Truncate(string.IsNullOrWhiteSpace(document.Title) ? document.Name : document.Title, 256);
            row.Summary = document.Summary is null ? null : Truncate(document.Summary, 1024);
            if (document.EffectiveFrom is { } from) row.EffectiveFrom = from;
            row.IsDraft = false;
        }

        row.TypeKey = document.Type;
        row.ContentHtml = document.Html;
        row.ContentHash = hash;
        row.ChangeKind = TruncateOrNull(document.ChangeKind, 16);
        row.FetchedAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The column is <c>char(64)</c>: anything but a 64-character hex digest is not stored.</summary>
    internal static string? NormaliseHash(string? hash) =>
        hash != null && Sha256Hex().IsMatch(hash) ? hash.ToLowerInvariant() : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? TruncateOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value, max);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
