using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Filters;

/// <summary>
/// Step-up re-authentication (#80): the cookie session must have confirmed the password or an
/// e-mailed code within <see cref="SessionSecurityOptions.ReauthMinutes"/> (default 15). A stolen
/// or forgotten-open session then cannot mint API tokens or change billing.
///
/// Without a fresh confirmation the action does not run and the answer is 403 with the code
/// <c>REAUTH_REQUIRED</c> — as JSON for a JSON/XHR caller, otherwise as the confirmation form
/// (also 403, header <c>X-Error-Code</c>), which returns the user to the page they came from.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireRecentAuthAttribute : Attribute, IAsyncActionFilter
{
    public const string ErrorCode = "REAUTH_REQUIRED";
    public const string ReauthPath = "/Account/Reauth";

    /// <summary>
    /// A stricter limit for this action, in minutes (deleting the account: 10, #217). The
    /// shorter of this and <see cref="SessionSecurityOptions.ReauthMinutes"/> applies; 0 = the
    /// configured window only.
    /// </summary>
    public int MaxMinutes { get; set; }

    /// <summary>The window for an action with <paramref name="maxMinutes"/>: never longer than the configured one.</summary>
    public static TimeSpan WindowFor(TimeSpan configured, int maxMinutes) =>
        maxMinutes > 0 && TimeSpan.FromMinutes(maxMinutes) < configured ? TimeSpan.FromMinutes(maxMinutes) : configured;

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<SessionSecurityOptions>>().Value;
        var now = services.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;

        if (IsFresh(SessionClaims.AuthTimeOf(context.HttpContext.User), now, WindowFor(options.ReauthWindow, MaxMinutes)))
        {
            return next();
        }

        context.Result = Refusal(context.HttpContext, services);
        return Task.CompletedTask;
    }

    public static bool IsFresh(DateTimeOffset? authTime, DateTimeOffset now, TimeSpan window) =>
        authTime is { } at && at <= now.AddMinutes(1) && now - at <= window;

    private static IActionResult Refusal(HttpContext http, IServiceProvider services)
    {
        http.Response.Headers["X-Error-Code"] = ErrorCode;
        var localizer = services.GetRequiredService<IStringLocalizer<SharedStrings>>();
        var returnUrl = ReturnUrlFor(http);

        if (WantsJson(http.Request))
        {
            return new ObjectResult(new
            {
                code = ErrorCode,
                message = localizer["Error_REAUTH_REQUIRED"].Value,
                reauthUrl = $"{http.Request.PathBase}{ReauthPath}?returnUrl={Uri.EscapeDataString(returnUrl)}",
            })
            { StatusCode = StatusCodes.Status403Forbidden };
        }

        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        return new ViewResult
        {
            ViewName = "~/Views/Account/Reauth.cshtml",
            StatusCode = StatusCodes.Status403Forbidden,
            ViewData = new ViewDataDictionary<ReauthViewModel>(metadata, new ModelStateDictionary())
            {
                Model = new ReauthViewModel { ReturnUrl = returnUrl, Required = true },
            },
        };
    }

    private static bool WantsJson(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where to go after confirming: the page the action was started from when it is ours (a
    /// POST cannot be replayed by a redirect), else the action's own path for a GET.
    /// </summary>
    private static string ReturnUrlFor(HttpContext http)
    {
        var request = http.Request;
        if (HttpMethods.IsGet(request.Method))
        {
            return $"{request.PathBase}{request.Path}{request.QueryString}";
        }
        if (Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && string.Equals(referer.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && (referer.IsDefaultPort ? null : referer.Port) == request.Host.Port)
        {
            return referer.PathAndQuery;
        }
        return $"{request.PathBase}/";
    }
}
