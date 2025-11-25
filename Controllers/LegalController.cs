using Microsoft.AspNetCore.Mvc;

namespace DotNetSigningServer.Controllers;

[Route("Legal")]
public class LegalController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("TermsOfService")]
    public IActionResult TermsOfService() => View("TermsOfService/Index");

    [HttpGet("PrivacyPolicy")]
    public IActionResult PrivacyPolicy() => View("PrivacyPolicy/Index");

    [HttpGet("DataProcessingAgreement")]
    public IActionResult DataProcessingAgreement() => View("DataProcessingAgreement/Index");

    [HttpGet("ServiceLevelAgreement")]
    public IActionResult ServiceLevelAgreement() => View("ServiceLevelAgreement/Index");

    [HttpGet("RefundPolicy")]
    public IActionResult RefundPolicy() => View("RefundPolicy/Index");

    [HttpGet("CookiesPolicy")]
    public IActionResult CookiesPolicy() => View("CookiesPolicy/Index");

    [HttpGet("OpenSourceNotices")]
    public IActionResult OpenSourceNotices() => View("OpenSourceNotices/Index");

    [HttpGet("License")]
    public IActionResult License() => View("License/Index");
}
