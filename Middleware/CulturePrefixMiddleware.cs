using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Localization;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Makes the URL the single source of truth for the page language.
///
/// Three jobs, in order:
/// 1. "/cs/pricing" is moved into PathBase, so routing still sees "/pricing" and
///    every generated link (Url.Action, RedirectToAction, the login redirect)
///    keeps the prefix without any controller knowing about locales.
/// 2. A visitor arriving at an unprefixed URL who has a remembered language, or
///    whose browser asks for one we publish, is redirected once to that language's
///    URL. Requests with no Accept-Language and no cookie — crawlers, curl — are
///    left on the English URL so the index never fills up with redirects.
/// 3. Legacy "?culture=xx" links are permanently redirected to the prefixed URL so
///    the two forms don't compete for the same content.
/// </summary>
public class CulturePrefixMiddleware
{
    /// <summary>Where the resolved culture is published for the request culture provider.</summary>
    public const string CultureItemKey = "P4PDF.Culture";

    private readonly RequestDelegate _next;

    public CulturePrefixMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        var request = context.Request;

        if (CultureUrls.TrySplit(request.Path, out var culture, out var rest))
        {
            request.PathBase = request.PathBase.Add($"/{culture}");
            request.Path = rest;
            context.Items[CultureItemKey] = culture;
            RememberChoice(context, culture);
            await _next(context);
            return;
        }

        context.Items[CultureItemKey] = CultureUrls.Default;

        if (IsRedirectCandidate(context))
        {
            // The response differs per visitor, so a shared cache must not serve
            // one visitor's language to the next.
            context.Response.Headers.Append("Vary", "Accept-Language, Cookie");

            var legacy = request.Query["culture"].ToString();
            if (CultureUrls.IsSupported(legacy))
            {
                context.Response.Redirect(TargetUrl(context, legacy, dropCultureQuery: true), permanent: true);
                return;
            }

            var preferred = PreferredCulture(context);
            if (preferred != null && !CultureUrls.IsDefault(preferred))
            {
                context.Response.Redirect(TargetUrl(context, preferred, dropCultureQuery: false), permanent: false);
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// A prefixed URL is an explicit choice — store it so the visitor keeps that
    /// language when they later land on an unprefixed link.
    /// </summary>
    private static void RememberChoice(HttpContext context, string culture)
    {
        var cookieValue = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture));
        if (string.Equals(context.Request.Cookies[CookieRequestCultureProvider.DefaultCookieName], cookieValue, StringComparison.Ordinal))
        {
            return;
        }

        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            cookieValue,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                HttpOnly = false,
                Path = "/",
            });
    }

    /// <summary>Remembered choice first, browser preference second.</summary>
    private static string? PreferredCulture(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieRequestCultureProvider.DefaultCookieName];
        if (!string.IsNullOrEmpty(cookie))
        {
            var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie)?.UICultures?.FirstOrDefault().Value;
            var language = parsed?.Split('-')[0].ToLowerInvariant();
            // A remembered choice is final either way: English stops the redirect.
            return CultureUrls.IsSupported(language) ? language : null;
        }

        return CultureUrls.MatchAcceptLanguage(context.Request.Headers.AcceptLanguage.ToString());
    }

    /// <summary>
    /// Only real page views are redirected: a GET/HEAD for HTML that isn't the
    /// REST API, an ops endpoint or a static asset.
    /// </summary>
    private static bool IsRedirectCandidate(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // Browsers ask for text/html on navigation; API clients and asset requests don't.
        if (!request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = request.Path;
        // /api/docs is a marketing page and is localized; the rest of /api is the REST surface.
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/api/docs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var excluded in ExcludedPrefixes)
        {
            if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] ExcludedPrefixes =
    {
        "/health", "/healthz", "/swagger", "/debug",
        "/css", "/js", "/img", "/lib", "/fonts",
        "/robots.txt", "/sitemap.xml", "/favicon.ico",
        // Stripe and the SPFx clients post back to fixed URLs; a locale prefix
        // on those would only add a hop.
        "/webhook",
    };

    private static string TargetUrl(HttpContext context, string culture, bool dropCultureQuery)
    {
        var request = context.Request;
        var query = request.QueryString;
        if (dropCultureQuery)
        {
            var kept = request.Query
                .Where(q => !string.Equals(q.Key, "culture", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(q.Key, "ui-culture", StringComparison.OrdinalIgnoreCase))
                .SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? string.Empty)}"))
                .ToList();
            query = kept.Count > 0 ? new QueryString("?" + string.Join("&", kept)) : QueryString.Empty;
        }

        var path = request.Path.HasValue ? request.Path.Value! : "/";
        var tail = path == "/" ? string.Empty : path;
        return $"{request.PathBase}{CultureUrls.Prefix(culture)}{tail}{query}";
    }
}

/// <summary>
/// Feeds the culture that <see cref="CulturePrefixMiddleware"/> resolved from the
/// URL into ASP.NET's localization. Registered as the only provider, so cookies
/// and Accept-Language can influence *which URL* a visitor lands on but never the
/// language of the page they were served.
/// </summary>
public class PathCultureProvider : Microsoft.AspNetCore.Localization.RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var culture = httpContext.Items[CulturePrefixMiddleware.CultureItemKey] as string ?? CultureUrls.Default;
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture, culture));
    }
}
