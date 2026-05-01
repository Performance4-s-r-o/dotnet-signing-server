using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace DotNetSigningServer.Controllers;

[Route("Legal")]
public class LegalController : Controller
{
    private readonly LegalDocumentsClient _cms;

    public LegalController(LegalDocumentsClient cms)
    {
        _cms = cms;
    }

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("TermsOfService")]
    public Task<IActionResult> TermsOfService(CancellationToken ct)
        => RenderAsync("terms-of-service", "TermsOfService/Index", ct);

    [HttpGet("PrivacyPolicy")]
    public Task<IActionResult> PrivacyPolicy(CancellationToken ct)
        => RenderAsync("privacy-policy", "PrivacyPolicy/Index", ct);

    [HttpGet("DataProcessingAgreement")]
    public Task<IActionResult> DataProcessingAgreement(CancellationToken ct)
        => RenderAsync("data-processing-agreement", "DataProcessingAgreement/Index", ct);

    [HttpGet("ServiceLevelAgreement")]
    public Task<IActionResult> ServiceLevelAgreement(CancellationToken ct)
        => RenderAsync("service-level-agreement", "ServiceLevelAgreement/Index", ct);

    [HttpGet("RefundPolicy")]
    public Task<IActionResult> RefundPolicy(CancellationToken ct)
        => RenderAsync("refund-policy", "RefundPolicy/Index", ct);

    [HttpGet("CookiesPolicy")]
    public Task<IActionResult> CookiesPolicy(CancellationToken ct)
        => RenderAsync("cookies-policy", "CookiesPolicy/Index", ct);

    [HttpGet("OpenSourceNotices")]
    public Task<IActionResult> OpenSourceNotices(CancellationToken ct)
        => RenderAsync("open-source-notices", "OpenSourceNotices/Index", ct);

    [HttpGet("License")]
    public Task<IActionResult> License(CancellationToken ct)
        => RenderAsync("license", "License/Index", ct);

    /// <summary>
    /// Try to render the CMS-managed version of a legal document; on miss
    /// or any failure, fall back to the existing static Razor view.
    /// </summary>
    private async Task<IActionResult> RenderAsync(string slug, string staticViewName, CancellationToken ct)
    {
        var locale = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        var rendered = await _cms.TryGetAsync(slug, locale, ct);

        if (rendered is null && locale != "en")
        {
            // English is the platform-default locale and the most likely to
            // be authored — fall back to it before resorting to static.
            rendered = await _cms.TryGetAsync(slug, "en", ct);
        }

        if (rendered is not null)
        {
            return View("Dynamic", rendered);
        }

        return View(staticViewName);
    }
}
