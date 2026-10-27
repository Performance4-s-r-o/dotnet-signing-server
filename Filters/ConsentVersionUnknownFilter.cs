using DotNetSigningServer.Resources;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Filters;

/// <summary>
/// A consent form whose document has no known version answers 503 and logs an error; nothing is
/// recorded. Retrying is safe: the cause is missing local data, not the request.
/// </summary>
public sealed class ConsentVersionUnknownFilter(
    IStringLocalizer<SharedStrings> localizer,
    ILogger<ConsentVersionUnknownFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ConsentVersionUnknownException unknown) return;
        logger.LogError(unknown, "[consents] refusing a consent form for {Document}: no version is known", unknown.Document);
        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentType = "text/plain; charset=utf-8",
            Content = localizer["ConsentVersionUnknown"].Value,
        };
        context.ExceptionHandled = true;
    }
}
