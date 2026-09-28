using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// The cookies this product sets, from the cookie audit of 2026-09-28: only strictly necessary
/// ones, no analytics or marketing (so no cookie banner). Shown by the cookies policy when the
/// service's declaration is not available (Docs module Off/Shadow, or no snapshot yet).
///
/// Keep in sync with the declaration in the service. Adding analytics or any other optional
/// cookie: see "Cookie banner" in README.md first.
/// </summary>
public static class AuditedCookies
{
    public const string Provider = "Performance4";

    /// <summary>Names in table order.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        ".AspNetCore.Cookies",
        ".AspNetCore.Antiforgery.*",
        ".AspNetCore.Mvc.CookieTempDataProvider",
        CookieRequestCultureProvider.DefaultCookieName,
    ];

    /// <summary>The audited cookies with purpose and duration in the current UI language.</summary>
    public static IReadOnlyList<CookieDeclarationCookie> For(IStringLocalizer<SharedStrings> l) =>
    [
        Necessary(Names[0], l["CookieAuthPurpose"], l["CookieAuthDuration"]),
        Necessary(Names[1], l["CookieAntiforgeryPurpose"], l["CookieDurationSession"]),
        Necessary(Names[2], l["CookieTempDataPurpose"], l["CookieDurationSession"]),
        Necessary(Names[3], l["CookieCulturePurpose"], l["CookieDurationOneYear"]),
    ];

    private static CookieDeclarationCookie Necessary(string name, string purpose, string duration) =>
        new(name, Provider, "necessary", purpose, duration, "first");
}
