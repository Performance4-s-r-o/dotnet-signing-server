using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DotNetSigningServer.Filters;

/// <summary>
/// Marks pages that must never be indexed, so _Layout can emit
/// <c>&lt;meta name="robots" content="noindex, nofollow"&gt;</c> and skip the
/// hreflang alternates.
///
/// Two sources: anything behind <see cref="AuthorizeAttribute"/> (dashboards,
/// billing, admin), plus the sign-in/sign-up forms, which are public but have no
/// business ranking. Doing it here rather than in each view means a new private
/// page is covered the moment it gets [Authorize], with nothing to remember.
/// </summary>
public class NoIndexPrivatePagesFilter : IResultFilter
{
    private static readonly string[] NoIndexPathPrefixes =
    {
        "/account",
        "/admin",
        "/apitokens",
        "/billing",
        "/requests",
        "/support",
        "/templates",
        "/template-builder",
        "/debug",
    };

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ViewResult view)
        {
            return;
        }

        var endpoint = context.HttpContext.GetEndpoint();
        var requiresAuth = endpoint?.Metadata.GetMetadata<IAuthorizeData>() != null
                           && endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null;

        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var isPrivatePath = NoIndexPathPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (requiresAuth || isPrivatePath)
        {
            view.ViewData["NoIndex"] = true;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
