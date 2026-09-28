using System.Net;
using System.Text.Json;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Document events (Docs module On):
/// <list type="bullet">
/// <item><c>document.published</c>, <c>document.minor_corrected</c>: drop the in-memory copy,
/// fetch the new text right away (English and Czech) into memory and the snapshot, and rebuild
/// the document's <c>docs:meta</c> entry.</item>
/// <item><c>document.scheduled</c>, <c>document.unscheduled</c>: only the <c>docs:meta</c> entry
/// (its <c>upcoming</c> part changes; the text in force does not).</item>
/// </list>
/// A failed fetch throws, so the inbox retries the event.
/// </summary>
public sealed class DocumentEventsHandler : IBackofficeEventHandler
{
    private readonly BackofficeDocumentsCache _cache;
    private readonly LegalDocumentRefresher _refresher;
    private readonly DocumentsMetaUpdater _meta;
    private readonly ILogger<DocumentEventsHandler> _logger;

    public DocumentEventsHandler(
        BackofficeDocumentsCache cache,
        LegalDocumentRefresher refresher,
        DocumentsMetaUpdater meta,
        ILogger<DocumentEventsHandler> logger)
    {
        _cache = cache;
        _refresher = refresher;
        _meta = meta;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } =
    [
        BackofficeEventTypes.DocumentPublished,
        BackofficeEventTypes.DocumentMinorCorrected,
        BackofficeEventTypes.DocumentScheduled,
        BackofficeEventTypes.DocumentUnscheduled,
    ];

    public async Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var type = DocumentType(evt.Data);
        if (type is null)
        {
            _logger.LogWarning("Backoffice event {EventId} ({EventType}) names no document type; ignored", evt.Id, evt.Type);
            return;
        }

        if (evt.Type is BackofficeEventTypes.DocumentPublished or BackofficeEventTypes.DocumentMinorCorrected
            && LegalSlugMap.SlugFor(type) != null)
        {
            foreach (var locale in LegalLocales.Snapshot)
            {
                _cache.Remove(type, locale);
                try
                {
                    await _refresher.RefreshAsync(type, locale, saveSnapshot: true, cancellationToken);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Withdrawn again before we got to it; nothing in force to show.
                    _logger.LogInformation("Backoffice event {EventId}: {Type} is no longer published", evt.Id, type);
                    break;
                }
            }
        }

        await _meta.RefreshTypeAsync(type, cancellationToken);
    }

    /// <summary><c>data.document.type</c>; null when missing.</summary>
    internal static string? DocumentType(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("document", out var document)
        && document.ValueKind == JsonValueKind.Object
        && document.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(type.GetString())
            ? type.GetString()
            : null;
}
