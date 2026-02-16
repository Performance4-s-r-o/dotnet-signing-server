using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace DotNetSigningServer.Middleware;

public class BodySizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LimitsOptions _options;
    private readonly ILogger<BodySizeLimitMiddleware> _logger;

    public BodySizeLimitMiddleware(RequestDelegate next, IOptions<LimitsOptions> options, ILogger<BodySizeLimitMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var limit = _options.RequestBodyLimitBytes;
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
            await context.Response.WriteAsJsonAsync(new { message = "Request exceeded limit." });
            return;
        }

        await _next(context);
    }
}
