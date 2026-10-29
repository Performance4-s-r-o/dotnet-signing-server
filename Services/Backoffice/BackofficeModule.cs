namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// Parts of the product that can be switched to the P4 Backoffice service one by one
/// (<c>P4Backoffice:Modules:&lt;name&gt;</c>).
/// </summary>
public enum BackofficeModule
{
    /// <summary>Legal documents (terms, privacy, …) and the cookie declaration.</summary>
    Docs,

    /// <summary>Consent records.</summary>
    Consents,

    /// <summary>Transactional e-mail.</summary>
    Email,

    /// <summary>Credit pricing and price-change notices.</summary>
    Pricing,

    /// <summary>Support tickets.</summary>
    Support,

    /// <summary>
    /// Payments (checkout, saved cards, invoices, auto-recharge). The global <c>Mode=On</c> never
    /// switches it on: it moves money. In Production it is On by default; its own setting
    /// or a global <c>Off</c> rolls it back.
    /// </summary>
    Billing,
}
