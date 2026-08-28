using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using DotNetSigningServer.Services;
using DotNetSigningServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly PdfTemplateService _templateService;
    private readonly AiOptions _aiOptions;
    private readonly AppOptions _appOptions;
    private readonly IWebHostEnvironment _env;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    public HomeController(ILogger<HomeController> logger, PdfTemplateService templateService, IOptions<AiOptions> aiOptions, IOptions<AppOptions> appOptions, IWebHostEnvironment env, IStringLocalizer<SharedStrings> localizer)
    {
        _logger = logger;
        _templateService = templateService;
        _aiOptions = aiOptions.Value;
        _appOptions = appOptions.Value;
        _env = env;
        _localizer = localizer;
    }

    [HttpGet("/debug/request")]
    public IActionResult DebugRequest()
    {
        if (!_env.IsDevelopment())
            return NotFound();

        return Ok(new
        {
            Scheme = Request.Scheme,
            Host = Request.Host.ToString(),
            IsHttps = Request.IsHttps,
            XForwardedProto = Request.Headers["X-Forwarded-Proto"].ToString(),
            XForwardedHost = Request.Headers["X-Forwarded-Host"].ToString(),
            XForwardedFor = Request.Headers["X-Forwarded-For"].ToString()
        });
    }

    [HttpGet("/")]
    public IActionResult Index()
    {
        ViewData["SignupSuccess"] = string.Equals(Request.Query["signup"], "success", StringComparison.OrdinalIgnoreCase);
        return View();
    }

    [HttpGet("/pricing")]
    public IActionResult Pricing()
    {
        return View();
    }

    [HttpGet("/contact")]
    public IActionResult Contact() => View();

    [HttpGet("/api/docs")]
    public IActionResult ApiDocs()
    {
        var baseUrl = BuildBaseUrl();
        ViewData["BaseUrl"] = baseUrl;
        return View(ApiDocsCatalog.All(ApiBase(baseUrl)));
    }

    /// <summary>
    /// One page per endpoint. Anchors on a single long page cannot rank on their
    /// own, so each endpoint gets its own URL, title and description.
    /// </summary>
    [HttpGet("/api/docs/{slug}")]
    public IActionResult ApiEndpointDocs(string slug)
    {
        var baseUrl = BuildBaseUrl();
        var apiBase = ApiBase(baseUrl);
        var endpoint = ApiDocsCatalog.Find(slug, apiBase);
        if (endpoint == null)
        {
            return NotFound();
        }

        // Resolve the prose here so <title>, the description and the structured
        // data all read in the visitor's language, same as the page body.
        var title = _localizer[endpoint.TitleKey].Value;
        var description = _localizer[endpoint.MetaKey].Value;

        ViewData["BaseUrl"] = baseUrl;
        ViewData["Title"] = title;
        ViewData["MetaDescription"] = description;
        ViewData["JsonLd"] = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "APIReference",
            ["name"] = title,
            ["description"] = description,
            ["url"] = $"{baseUrl.TrimEnd('/')}/api/docs/{endpoint.Slug}",
            ["programmingModel"] = "REST",
            ["assemblyVersion"] = "v1",
            ["isPartOf"] = new Dictionary<string, object?>
            {
                ["@type"] = "WebSite",
                ["name"] = "Performance4PDF API Documentation",
                ["url"] = $"{baseUrl.TrimEnd('/')}/api/docs",
            },
        });

        return View("ApiEndpointDoc", endpoint);
    }

    private static string ApiBase(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? "/api" : $"{trimmed}/api";
    }

    [HttpGet("/template-builder")]
    [Authorize]
    public IActionResult TemplateBuilder()
    {
        var aiEnabled = _aiOptions.Enabled
                        && string.Equals(_aiOptions.Provider, "google", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(_aiOptions.Google?.ApiKey);
        ViewData["AiDetectEnabled"] = aiEnabled;
        return View();
    }

    [HttpGet("/templates")]
    [Authorize]
    public async Task<IActionResult> Templates()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return RedirectToAction("SignIn", "Account");
        }

        var templates = await _templateService.ListTemplatesAsync(userId);
        return View(templates);
    }

    [HttpGet("/templates/{templateId:guid}/docs")]
    [Authorize]
    public async Task<IActionResult> TemplateDocs(Guid templateId)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return RedirectToAction("SignIn", "Account");
        }

        try
        {
            var template = await _templateService.GetTemplateAsync(templateId, userId);
            ViewData["BaseUrl"] = BuildBaseUrl();
            return View(template);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Kept for links and bookmarks made before the language moved into the path.
    /// The cookie is still written so an unprefixed entry point sends the visitor
    /// to the right language, but the prefixed URL is what actually renders it.
    /// </summary>
    [HttpPost("/set-language")]
    [ValidateAntiForgeryToken]
    public IActionResult SetLanguage(string culture, string? returnUrl)
    {
        if (!CultureUrls.IsSupported(culture))
        {
            culture = CultureUrls.Default;
        }

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                HttpOnly = false,
                Path = "/",
            });

        // returnUrl arrives with whatever prefix the page had; swap it for the one
        // being switched to instead of stacking a second prefix on top.
        var target = "/";
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var split = returnUrl.Split('?', 2);
            var path = split[0];
            var rest = CultureUrls.StripLocale(path);
            target = CultureUrls.Absolute(string.Empty, rest.Value ?? "/", culture)
                     + (split.Length > 1 ? "?" + split[1] : string.Empty);
        }
        else
        {
            target = CultureUrls.Absolute(string.Empty, "/", culture);
        }

        return LocalRedirect(target.Length == 0 ? "/" : target);
    }

    private string BuildBaseUrl() => _appOptions.BaseUrl;
}
