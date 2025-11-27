using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace DotNetSigningServer.Middleware;

public class BodySizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LimitsOptions _options;

    public BodySizeLimitMiddleware(RequestDelegate next, IOptions<LimitsOptions> options)
    {
        _next = next;
        _options = options.Value;
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
            Console.WriteLine("body size limit exceeded");
            await context.Response.WriteAsJsonAsync(new { message = "Request exceeded limit." });
            return;
        }

        await _next(context);
    }
}
