using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>cookie_declaration.published</c> (Docs module On): refetches the declaration in every
/// legal language right away (ignoring the ETag) into memory and <c>BackofficeState</c>, so the
/// table on the cookies page shows the new version within the polling interval. A failed fetch
/// throws, so the inbox retries the event.
/// </summary>
public sealed class CookieDeclarationEventsHandler : IBackofficeEventHandler
{
    private readonly CookieDeclarationReader _reader;

    public CookieDeclarationEventsHandler(CookieDeclarationReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.CookieDeclarationPublished];

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken) =>
        _reader.RefreshAllAsync(force: true, cancellationToken);
}

/// <summary>
/// Rebuilds the cookie declaration snapshot at startup (<see cref="CookieDeclarationWarmup"/>)
/// and when events may have been missed (<c>window_clamped</c>). Docs module On only.
/// </summary>
public sealed class CookieDeclarationResync : IBackofficeResync
{
    private readonly CookieDeclarationReader _reader;

    public CookieDeclarationResync(CookieDeclarationReader reader)
    {
        _reader = reader;
    }

    public string Name => "cookie-declaration";

    public Task ResyncAsync(string reason, CancellationToken cancellationToken) =>
        _reader.RefreshAllAsync(force: false, cancellationToken);
}

/// <summary>
/// Startup warm-up of the cookie declaration (Docs module On): fetches it in the background, so
/// the first cookies page already has the table. Never blocks startup; retries a few times,
/// after that events and the page's own background refresh take over.
/// </summary>
public sealed class CookieDeclarationWarmup : BackgroundService
{
    private readonly CookieDeclarationReader _reader;
    private readonly TimeProvider _time;
    private readonly ILogger<CookieDeclarationWarmup> _logger;

    public CookieDeclarationWarmup(CookieDeclarationReader reader, TimeProvider time, ILogger<CookieDeclarationWarmup> logger)
    {
        _reader = reader;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        foreach (var delay in Documents.LegalDocumentsWarmup.Delays)
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, stoppingToken);
                await _reader.RefreshAllAsync(force: false, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[cookies] cookie declaration warm-up failed; retrying later");
            }
        }
        _logger.LogWarning("[cookies] cookie declaration warm-up gave up; it is refreshed on events and on page views");
    }
}
