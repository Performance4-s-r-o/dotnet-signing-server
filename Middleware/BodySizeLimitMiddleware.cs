using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace DotNetSigningServer.Middleware;

public class BodySizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LimitsOptions _options;
    private readonly ILogger<BodySizeLimitMiddleware> _logger;

    private readonly RateLimitOptions _rate;

    public BodySizeLimitMiddleware(
        RequestDelegate next,
        IOptions<LimitsOptions> options,
        IOptions<RateLimitOptions> rate,
        ILogger<BodySizeLimitMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _rate = rate.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Documents are 40 MB; everything else a browser form or a webhook can carry is much
        // smaller (#73). The form pages never take a file.
        var path = context.Request.Path;
        var limit = path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(_options.RequestBodyLimitBytes, _rate.WebhookBodyLimitBytes)
            : path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                ? _options.RequestBodyLimitBytes
                : Math.Min(_options.RequestBodyLimitBytes, _rate.FormBodyLimitBytes);
        var contentLength = context.Request.ContentLength;

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        if (contentLength.HasValue && contentLength.Value > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            _logger.LogWarning("Body size limit exceeded: {ContentLength} > {Limit} for {Path}", contentLength.Value, limit, context.Request.Path);
            var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
            var localizer = factory.Create(typeof(SharedStrings));
            await context.Response.WriteAsJsonAsync(new { message = localizer["RequestExceededLimit"].Value });
            return;
        }

        await _next(context);
    }
}
