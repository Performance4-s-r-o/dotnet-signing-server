using System.Net;
using System.Net.Http.Headers;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>Result of a conditional <c>GET /v1/pricing/current</c>.</summary>
/// <param name="Body">The response body; null when the service answered <c>304 Not Modified</c>.</param>
public sealed record PricingFetch(string? Body, string? ETag)
{
    public bool NotModified => Body is null;
}

/// <summary>
/// Read-only client of <c>GET /v1/pricing/current</c> and <c>/v1/pricing/upcoming</c> of the P4 Backoffice service.
///
/// Vendored like <c>BackofficeDocumentsClient</c>: the default build does not include
/// <c>P4.Backoffice.Sdk</c> and tests run without it. TODO(P4.Backoffice.Sdk): once the
/// package is restored in CI, call the SDK's generated client (<c>V1.Pricing.Current</c>) here
/// and keep this class as the adapter — callers only see <see cref="PricingFetch"/>.
///
/// Only background code calls it (<see cref="PricingSnapshotRefresher"/>, <see cref="PricingUpcomingCheck"/>). Throws
/// <see cref="HttpRequestException"/> (or <see cref="TaskCanceledException"/> on timeout).
/// </summary>
public sealed class BackofficePricingClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.Pricing";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClients;

    public BackofficePricingClient(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    public async Task<PricingFetch> GetCurrentAsync(string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/pricing/current");
        if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && etag != null)
        {
            return new PricingFetch(null, etag);
        }
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PricingFetch(body, response.Headers.ETag?.ToString());
    }

    /// <summary><c>GET /v1/pricing/upcoming</c>: the body (<c>PriceBookUpcoming</c>) as JSON.</summary>
    public async Task<string> GetUpcomingAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClients.CreateClient(HttpClientName).GetAsync("v1/pricing/upcoming", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
