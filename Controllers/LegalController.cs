using DotNetSigningServer.Services.Legal;
using Microsoft.AspNetCore.Mvc;

namespace DotNetSigningServer.Controllers;

[Route("Legal")]
public class LegalController : Controller
{
    /// <summary>ViewData key of the cookie declaration shown under the cookies policy.</summary>
    public const string CookieDeclarationKey = "CookieDeclaration";

    private readonly ILegalDocumentSource _documents;
    private readonly ICookieDeclarationSource _cookieDeclaration;

    public LegalController(ILegalDocumentSource documents, ICookieDeclarationSource cookieDeclaration)
    {
        _documents = documents;
        _cookieDeclaration = cookieDeclaration;
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
        => RenderAsync(CookiesPolicySlug, "CookiesPolicy/Index", ct);

    private const string CookiesPolicySlug = "cookies-policy";

    [HttpGet("OpenSourceNotices")]
    public Task<IActionResult> OpenSourceNotices(CancellationToken ct)
        => RenderAsync("open-source-notices", "OpenSourceNotices/Index", ct);

    [HttpGet("License")]
    public Task<IActionResult> License(CancellationToken ct)
        => RenderAsync("license", "License/Index", ct);

    /// <summary>
    /// Render the managed version of a legal document (hand-maintained rows, or the P4
    /// Backoffice service with its snapshot — see <see cref="ILegalDocumentSource"/>); when
    /// there is none, fall back to the existing static Razor view.
    /// </summary>
    private async Task<IActionResult> RenderAsync(string slug, string staticViewName, CancellationToken ct)
    {
        var locale = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        var rendered = await _documents.GetAsync(slug, locale, ct);

        if (rendered is not null)
        {
            if (slug == CookiesPolicySlug)
            {
                // Docs On: the service's declaration (memory or snapshot, never a live call).
                // Null otherwise; the page then has no table.
                ViewData[CookieDeclarationKey] = await _cookieDeclaration.GetAsync(locale, ct);
            }
            return View("Dynamic", rendered);
        }

        return View(staticViewName);
    }
}
