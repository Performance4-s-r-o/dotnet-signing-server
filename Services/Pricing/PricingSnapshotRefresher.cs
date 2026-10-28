using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>Outcome of one refresh.</summary>
public enum PricingRefreshOutcome
{
    /// <summary>A new body was stored (it may still describe the same version).</summary>
    Updated,

    /// <summary><c>304 Not Modified</c>: the snapshot is current.</summary>
    NotModified,
}

/// <summary>
/// Keeps the price-list snapshot (<c>BackofficeState["pricing:current"]</c> and
/// <see cref="PricingSnapshotHolder"/>) up to date from <c>GET /v1/pricing/current</c>, with
/// <c>If-None-Match</c>. Runs in the background only: at startup, hourly
/// (<see cref="PricingSnapshotWorker"/>), after <c>price.effective</c>
/// (<see cref="Backoffice.Handlers.PriceEffectiveHandler"/>) and on <c>window_clamped</c> (<see cref="PricingResync"/>).
/// </summary>
public sealed class PricingSnapshotRefresher
{
    private readonly BackofficePricingClient _client;
    private readonly PricingSnapshotHolder _holder;
    private readonly StripePriceResolver _resolver;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PricingSnapshotRefresher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PricingSnapshotRefresher(
        BackofficePricingClient client,
        PricingSnapshotHolder holder,
        StripePriceResolver resolver,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<PricingSnapshotRefresher> logger)
    {
        _client = client;
        _holder = holder;
        _resolver = resolver;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Loads the stored snapshot into memory unless one is there already. Local database only.</summary>
    public async Task<bool> LoadStoredAsync(CancellationToken cancellationToken)
    {
        if (_holder.Current != null) return true;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = PricingSnapshot.TryDeserialize(
            await BackofficeStateStore.GetAsync(db, BackofficeStateKeys.PricingCurrent, cancellationToken));
        if (stored is null) return false;

        if (_holder.Current is null)
        {
            _holder.Set(stored);
            _logger.LogInformation("[pricing] loaded stored price list v{Version} (fetched {FetchedAt:u})", stored.Version, stored.FetchedAt);
        }
        return true;
    }

    /// <summary>
    /// Fetches the price list (conditionally when <paramref name="conditional"/>), stores and
    /// publishes it. Throws when the service or the database fails; the snapshot is kept.
    /// </summary>
    public async Task<PricingRefreshOutcome> RefreshAsync(string reason, bool conditional, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = _holder.Current;
            var fetch = await _client.GetCurrentAsync(conditional ? previous?.ETag : null, cancellationToken);
            if (fetch.NotModified && previous != null)
            {
                _logger.LogDebug("[pricing] price list v{Version} unchanged ({Reason})", previous.Version, reason);
                return PricingRefreshOutcome.NotModified;
            }

            PricingSnapshot snapshot;
            try
            {
                snapshot = PricingSnapshot.FromResponse(fetch.Body!, fetch.ETag, _time.GetUtcNow());
            }
            catch (JsonException ex)
            {
                // One exception type for "the service did not give us a usable answer".
                throw new HttpRequestException("Backoffice price list response is not a price list", ex);
            }

            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.PricingCurrent, snapshot.Serialize(), snapshot.FetchedAt, cancellationToken);
            }
            _holder.Set(snapshot);

            if (previous is null || previous.Version != snapshot.Version)
            {
                // Stripe Prices cached for the old version must not be compared with new amounts.
                _resolver.Invalidate();
                _logger.LogInformation("[pricing] price list v{Version} stored ({Reason})", snapshot.Version, reason);
            }
            return PricingRefreshOutcome.Updated;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Pricing On: loads the stored snapshot before the app serves requests (local database
/// only), then refreshes it from the service in the background — right away and every
/// <see cref="Interval"/>, sooner after a failure. Never blocks startup on the service.
/// </summary>
public sealed class PricingSnapshotWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly PricingSnapshotRefresher _refresher;
    private readonly TimeProvider _time;
    private readonly ILogger<PricingSnapshotWorker> _logger;

    public PricingSnapshotWorker(PricingSnapshotRefresher refresher, TimeProvider time, ILogger<PricingSnapshotWorker> logger)
    {
        _refresher = refresher;
        _time = time;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _refresher.LoadStoredAsync(cancellationToken))
            {
                _logger.LogInformation("[pricing] no stored price list yet; configured prices until the first refresh");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[pricing] stored price list could not be loaded; configured prices until the first refresh");
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var reason = "startup";
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Interval;
            try
            {
                await _refresher.RefreshAsync(reason, conditional: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                delay = RetryAfterFailure;
                _logger.LogWarning(ex, "[pricing] price list refresh ({Reason}) failed; keeping the current prices", reason);
            }

            reason = "hourly";
            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Rebuilds the price-list snapshot when events may have been missed (<c>window_clamped</c>).</summary>
public sealed class PricingResync : IBackofficeResync
{
    private readonly PricingSnapshotRefresher _refresher;

    public PricingResync(PricingSnapshotRefresher refresher)
    {
        _refresher = refresher;
    }

    public string Name => "pricing";

    public Task ResyncAsync(string reason, CancellationToken cancellationToken) =>
        _refresher.RefreshAsync(reason, conditional: false, cancellationToken);
}
