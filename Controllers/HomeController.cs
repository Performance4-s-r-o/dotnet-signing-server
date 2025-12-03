using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using DotNetSigningServer.Services;
using DotNetSigningServer.Models;
using Microsoft.AspNetCore.Authorization;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly PdfTemplateService _templateService;
    private readonly AiOptions _aiOptions;
    private readonly AppOptions _appOptions;

    public HomeController(ILogger<HomeController> logger, PdfTemplateService templateService, IOptions<AiOptions> aiOptions, IOptions<AppOptions> appOptions)
    {
        _logger = logger;
        _templateService = templateService;
        _aiOptions = aiOptions.Value;
        _appOptions = appOptions.Value;
    }

    [HttpGet("/debug/request")]
    public IActionResult DebugRequest()
    {
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
        ViewData["BaseUrl"] = BuildBaseUrl();
        return View();
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

    private string BuildBaseUrl()
    {
        var configured = _appOptions.FqdnServerName?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            var scheme = Request?.IsHttps == true ? "https" : "http";
            return $"{scheme}://{configured}";
        }

        var fallbackScheme = Request?.Scheme ?? "https";
        var host = Request?.Host.Value ?? "localhost";
        return $"{fallbackScheme}://{host}";
    }
}
