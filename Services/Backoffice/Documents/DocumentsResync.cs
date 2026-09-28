using System.Net;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Services.Backoffice.Documents;

/// <summary>
/// Rebuilds everything the Docs module keeps locally: <c>docs:meta</c>, and the in-memory copy
/// and <c>LegalDocuments</c> snapshot of every document this product shows, in English and
/// Czech. Runs at startup (<see cref="LegalDocumentsWarmup"/>) and when event polling reports
/// that events may have been missed (<c>window_clamped</c>). Docs module On only.
/// </summary>
public sealed class DocumentsResync : IBackofficeResync
{
    private readonly DocumentsMetaUpdater _meta;
    private readonly LegalDocumentRefresher _refresher;
    private readonly ILogger<DocumentsResync> _logger;

    public DocumentsResync(DocumentsMetaUpdater meta, LegalDocumentRefresher refresher, ILogger<DocumentsResync> logger)
    {
        _meta = meta;
        _refresher = refresher;
        _logger = logger;
    }

    public string Name => "documents";

    /// <summary>Tries every step; throws at the end when one of them failed (the caller retries).</summary>
    public async Task ResyncAsync(string reason, CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            await _meta.RefreshAllAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            failures++;
            _logger.LogWarning(ex, "[legal-docs] docs:meta resync ({Reason}) failed", reason);
        }

        var refreshed = 0;
        foreach (var type in LegalSlugMap.Types)
        {
            foreach (var locale in LegalLocales.Snapshot)
            {
                try
                {
                    await _refresher.RefreshAsync(type, locale, saveSnapshot: true, cancellationToken);
                    refreshed++;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not published in the service (yet): the local row or the Razor view stays.
                    _logger.LogInformation("[legal-docs] {Type} is not published in the service; keeping the local text", type);
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    _logger.LogWarning(ex, "[legal-docs] resync ({Reason}) of {Type}/{Locale} failed", reason, type, locale);
                }
            }
        }

        _logger.LogInformation("[legal-docs] resync ({Reason}): {Refreshed} document texts refreshed, {Failures} failures",
            reason, refreshed, failures);
        if (failures > 0)
        {
            throw new InvalidOperationException($"Documents resync ({reason}) failed in {failures} step(s).");
        }
    }
}

/// <summary>
/// Startup warm-up of the Docs module (On): runs <see cref="DocumentsResync"/> in the
/// background, so the first pages already have the service's texts. Never blocks startup;
/// retries a few times, after that webhooks, polling and the pages' own background refresh
/// take over.
/// </summary>
public sealed class LegalDocumentsWarmup : BackgroundService
{
    internal static readonly TimeSpan[] Delays =
    [
        TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<LegalDocumentsWarmup> _logger;

    public LegalDocumentsWarmup(IServiceScopeFactory scopes, TimeProvider time, ILogger<LegalDocumentsWarmup> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        foreach (var delay in Delays)
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, stoppingToken);
                using var scope = _scopes.CreateScope();
                var resync = scope.ServiceProvider.GetRequiredService<DocumentsResync>();
                await resync.ResyncAsync("startup", stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[legal-docs] warm-up failed; retrying later");
            }
        }
        _logger.LogWarning("[legal-docs] warm-up gave up; documents are refreshed on events and on page views");
    }
}
