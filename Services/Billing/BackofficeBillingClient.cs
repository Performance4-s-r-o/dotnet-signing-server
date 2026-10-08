using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// </summary>
public sealed class BackofficeBillingClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.Billing";

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The service calls Stripe inline (10 s timeout of its own), so this is a little longer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<BackofficeBillingClient> _logger;
    private readonly Func<int, TimeSpan> _delay;

    public BackofficeBillingClient(IHttpClientFactory httpClients, ILogger<BackofficeBillingClient> logger)
        : this(httpClients, logger, BillingRetryPolicy.Delay)
    {
    }

    /// <summary>Tests: <paramref name="delay"/> replaces the wait between attempts.</summary>
    internal BackofficeBillingClient(IHttpClientFactory httpClients, ILogger<BackofficeBillingClient> logger, Func<int, TimeSpan> delay)
    {
        _httpClients = httpClients;
        _logger = logger;
        _delay = delay;
    }

    public Task<BillingApiResult> PutCustomerAsync(string customerRef, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}", body, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> GetCustomerAsync(string customerRef, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}", null, null, ct, attempts: 1);

    public Task<BillingApiResult> DetachPaymentMethodsAsync(string customerRef, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"v1/billing/customers/{Uri.EscapeDataString(customerRef)}/payment-methods", null, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> CreatePortalSessionAsync(object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/portal-sessions", body, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> ListInvoicesAsync(string customerRef, int limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Get,
            $"v1/billing/invoices?customer_ref={Uri.EscapeDataString(customerRef)}&limit={Math.Clamp(limit, 1, 100)}",
            null, null, ct, attempts: 1);

    public Task<BillingApiResult> CreateCheckoutAsync(object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/checkout-sessions", body, NewKey(), ct, attempts: 1);

    public Task<BillingApiResult> GetCheckoutAsync(string sessionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/billing/checkout-sessions/{Uri.EscapeDataString(sessionId)}", null, null, ct, attempts: 1);

    /// <summary>Off-session charge with the caller's key: repeating it can never charge twice.</summary>
    public Task<BillingApiResult> CreateChargeAsync(object body, string idempotencyKey, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/charges", body, idempotencyKey, ct, BillingRetryPolicy.MaxAttempts);

    private static string NewKey() => Guid.NewGuid().ToString("D");

    private async Task<BillingApiResult> SendAsync(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct, int attempts)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, Json);
        var result = BillingApiResult.NoAnswer();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                _logger.LogInformation("Billing API {Method} {Path}: {Result}; repeating with the same idempotency key",
                    method, PathForLog(path), result.Describe());
                await Task.Delay(_delay(attempt), ct);
            }

            result = await AttemptAsync(method, path, json, idempotencyKey, ct);
            if (!BillingRetryPolicy.RetryWithSameKey(result)) break;
        }
        return result;
    }

    private async Task<BillingApiResult> AttemptAsync(HttpMethod method, string path, string? json, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (idempotencyKey != null) request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return BillingApiResult.From((int)response.StatusCode, text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            _logger.LogWarning("Billing API {Method} {Path}: no answer ({Error})", method, PathForLog(path), ex.GetType().Name);
            return BillingApiResult.NoAnswer();
        }
    }

    /// <summary>The path without its query (which names the customer).</summary>
    private static string PathForLog(string path)
    {
        var query = path.IndexOf('?');
        return query < 0 ? path : path[..query];
    }
}
