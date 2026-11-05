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
    public Task<IActionResult> TermsOfService(CancellationToken ct) => RenderAsync("terms-of-service", ct);

    [HttpGet("PrivacyPolicy")]
    public Task<IActionResult> PrivacyPolicy(CancellationToken ct) => RenderAsync("privacy-policy", ct);

    [HttpGet("DataProcessingAgreement")]
    public Task<IActionResult> DataProcessingAgreement(CancellationToken ct) => RenderAsync("data-processing-agreement", ct);

    [HttpGet("ServiceLevelAgreement")]
    public Task<IActionResult> ServiceLevelAgreement(CancellationToken ct) => RenderAsync("service-level-agreement", ct);

    [HttpGet("RefundPolicy")]
    public Task<IActionResult> RefundPolicy(CancellationToken ct) => RenderAsync("refund-policy", ct);

    [HttpGet("CookiesPolicy")]
    public Task<IActionResult> CookiesPolicy(CancellationToken ct) => RenderAsync(CookiesPolicySlug, ct);

    private const string CookiesPolicySlug = "cookies-policy";

    // Licence obligations of this open-source server: static pages, not documents of the service.
    [HttpGet("OpenSourceNotices")]
    public IActionResult OpenSourceNotices() => View("OpenSourceNotices/Index");

    [HttpGet("License")]
    public IActionResult License() => View("License/Index");

    /// <summary>
    /// Render a document of the service (its cached copy or the snapshot — see
    /// <see cref="ILegalDocumentSource"/>). There is no static text: without a document the
    /// answer is 503 and a page saying so.
    /// </summary>
    private async Task<IActionResult> RenderAsync(string slug, CancellationToken ct)
    {
        var locale = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        var rendered = await _documents.GetAsync(slug, locale, ct);

        if (rendered is null)
        {
            Response.Headers.RetryAfter = "60";
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View("Unavailable");
        }

        if (slug == CookiesPolicySlug)
        {
            // Docs On: the service's declaration (memory or snapshot, never a live call).
            // Null otherwise; the page then lists the audited cookies.
            ViewData[CookieDeclarationKey] = await _cookieDeclaration.GetAsync(locale, ct);
        }
        return View("Dynamic", rendered);
    }
}
