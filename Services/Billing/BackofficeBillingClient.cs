using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetSigningServer.Services.Ops;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// HTTP client of the P4 Backoffice billing API (<c>/v1/billing/*</c>), written from the
/// service's OpenAPI document — no SDK dependency, so every build can use it.
///
/// Every write carries an <c>Idempotency-Key</c>: a new one per call unless the caller passes
/// its own (an off-session charge keeps its key across attempts). Calls made while a user
/// waits are sent once (bounded by <see cref="RequestTimeout"/>); the background charge is sent
/// once more with the same key when it got no answer or the service says to repeat it
/// (<see cref="BillingRetryPolicy"/>). Answers are returned, not thrown; only cancellation by
/// the caller throws.
///
/// Outages: the invoice list times out after <see cref="ReadTimeout"/> (other calls keep the
/// client's 15 s: the service calls Stripe inline); <see cref="BillingCircuitBreaker"/>
/// (one for reads, one for writes) stops sending after repeated failures (a refused call is "no answer", never a success).
/// Only the invoice list is cached: a fresh copy for <see cref="InvoicesFresh"/>, and a stale one
/// up to <see cref="InvoicesStale"/> when the service does not answer (display only). A checkout
/// or customer read decides about money, so it is never served from the cache.
/// </summary>
public sealed class BackofficeBillingClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.Billing";

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The service calls Stripe inline (10 s timeout of its own), so this is a little longer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The invoice list (display only) waits less; checkout and customer reads keep 15 s.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan InvoicesFresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InvoicesStale = TimeSpan.FromMinutes(15);
    private const int MaxCachedLists = 500;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<BackofficeBillingClient> _logger;
    private readonly Func<int, TimeSpan> _delay;
    private readonly TimeProvider _time;
    private readonly BillingCircuitBreaker _breaker;
    private readonly BillingCircuitBreaker _readBreaker;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, BillingApiResult Result)> _invoices = new();

    public BackofficeBillingClient(IHttpClientFactory httpClients, ILogger<BackofficeBillingClient> logger, TimeProvider time, IOpsAlerter? alerter = null)
        : this(httpClients, logger, BillingRetryPolicy.Delay, time, alerter)
    {
    }

    /// <summary>Tests: <paramref name="delay"/> replaces the wait between attempts.</summary>
    internal BackofficeBillingClient(
        IHttpClientFactory httpClients,
        ILogger<BackofficeBillingClient> logger,
        Func<int, TimeSpan> delay,
        TimeProvider? time = null,
        IOpsAlerter? alerter = null)
    {
        _httpClients = httpClients;
        _logger = logger;
        _delay = delay;
        _time = time ?? TimeProvider.System;
        // Separate breakers: slow reads must not close checkout and charges.
        _breaker = new BillingCircuitBreaker(_time);
        _breaker.Opened += () =>
        {
            _logger.LogError("Billing API unavailable: writes are refused for {Seconds} s after repeated failures",
                (int)BillingCircuitBreaker.OpenFor.TotalSeconds);
            alerter?.Alert("billing-api-down",
                "Billing API unavailable: payments are refused until it answers again.");
        };
        _readBreaker = new BillingCircuitBreaker(_time);
        _readBreaker.Opened += () =>
        {
            _logger.LogError("Billing API reads unavailable: refused for {Seconds} s after repeated failures",
                (int)BillingCircuitBreaker.OpenFor.TotalSeconds);
            alerter?.Alert("billing-api-reads-down", "Billing API reads are failing; payment status cannot be confirmed.");
        };
    }

    /// <summary>Breaker of writes (checkout, charge, customer, portal).</summary>
    /// <summary>Tests shorten it.</summary>
    internal TimeSpan InvoicesTimeout { get; set; } = ReadTimeout;

    internal BillingCircuitBreaker Breaker => _breaker;

    /// <summary>Breaker of reads (GET); never affects writes.</summary>
    internal BillingCircuitBreaker ReadBreaker => _readBreaker;

    private BillingCircuitBreaker BreakerFor(HttpMethod method) => method == HttpMethod.Get ? _readBreaker : _breaker;

    public Task<BillingApiResult> PutCustomerAsync(string customerRef, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}", body, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> GetCustomerAsync(string customerRef, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}", null, null, ct, attempts: 1);

    public Task<BillingApiResult> DetachPaymentMethodsAsync(string customerRef, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}/payment-methods", null, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> CreatePortalSessionAsync(object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/portal-sessions", body, NewKey(), ct, attempts: 1);

    public async Task<BillingApiResult> ListInvoicesAsync(string customerRef, int limit, CancellationToken ct)
    {
        var path = $"v1/billing/invoices?customer_ref={Uri.EscapeDataString(customerRef)}&limit={Math.Clamp(limit, 1, 100)}";
        var now = _time.GetUtcNow();
        if (_invoices.TryGetValue(path, out var cached) && now - cached.At < InvoicesFresh) return cached.Result;

        var result = await SendAsync(HttpMethod.Get, path, null, null, ct, attempts: 1, timeout: InvoicesTimeout);
        if (result.IsSuccess)
        {
            if (_invoices.Count >= MaxCachedLists) PruneInvoices(now);
            _invoices[path] = (now, result);
        }
        else if (BillingCircuitBreaker.IsFailure(result) && cached.Result is not null && now - cached.At < InvoicesStale)
        {
            // Display only; the failure itself is already logged by the attempt.
            _logger.LogWarning("Billing API GET {Path}: serving the invoice list from {Age} s ago", PathForLog(path), (int)(now - cached.At).TotalSeconds);
            return cached.Result;
        }
        return result;
    }

    private void PruneInvoices(DateTimeOffset now)
    {
        foreach (var (key, entry) in _invoices)
        {
            if (now - entry.At >= InvoicesStale) _invoices.TryRemove(key, out _);
        }
        if (_invoices.Count >= MaxCachedLists) _invoices.Clear();
    }

    public Task<BillingApiResult> CreateCheckoutAsync(object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/checkout-sessions", body, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> GetCheckoutAsync(string sessionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/billing/checkout-sessions/{Uri.EscapeDataString(sessionId)}", null, null, ct, attempts: 1);

    /// <summary>Off-session charge with the caller's key: repeating it can never charge twice.</summary>
    public Task<BillingApiResult> CreateChargeAsync(object body, string idempotencyKey, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/charges", body, idempotencyKey, ct, BillingRetryPolicy.MaxAttempts);

    private static string NewKey() => Guid.NewGuid().ToString("D");

    private async Task<BillingApiResult> SendAsync(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct, int attempts, TimeSpan? timeout = null)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, Json);
        var result = BillingApiResult.NoAnswer();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                // An open breaker: no wait, the outcome stays "unknown".
                if (BreakerFor(method).IsOpen) break;
                _logger.LogInformation("Billing API {Method} {Path}: {Result}; repeating with the same idempotency key",
                    method, PathForLog(path), result.Describe());
                await Task.Delay(_delay(attempt), ct);
            }

            result = await AttemptAsync(method, path, json, idempotencyKey, ct, timeout);
            if (!BillingRetryPolicy.RetryWithSameKey(result)) break;
        }
        return result;
    }

    private async Task<BillingApiResult> AttemptAsync(HttpMethod method, string path, string? json, string? idempotencyKey, CancellationToken ct, TimeSpan? timeoutAfter)
    {
        var breaker = BreakerFor(method);
        if (breaker.IsOpen)
        {
            _logger.LogWarning("Billing API {Method} {Path}: not sent, the service did not answer recently", method, PathForLog(path));
            return BillingApiResult.NoAnswer();
        }

        using var request = new HttpRequestMessage(method, path);
        if (idempotencyKey != null) request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutAfter is { } after) timeout.CancelAfter(after);

        BillingApiResult result;
        try
        {
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            result = BillingApiResult.From((int)response.StatusCode, text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Billing API {Method} {Path}: no answer ({Error})", method, PathForLog(path), ex.GetType().Name);
            result = BillingApiResult.NoAnswer();
        }
        breaker.Record(result);
        return result;
    }

    /// <summary>The path without its query (which names the customer).</summary>
    private static string PathForLog(string path)
    {
        var query = path.IndexOf('?');
        return query < 0 ? path : path[..query];
    }
}
