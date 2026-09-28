namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Variables of each e-mail template, built in one place for every caller. The names are the
/// local <c>{{…}}</c> placeholders and the service template's schema properties (all strings,
/// all required) — <see cref="Names"/> is the contract both sides are checked against.
/// </summary>
public static class EmailTemplateVariables
{
    /// <summary>Variables every template needs, by template key.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Names =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [EmailTemplateId.EmailVerification] = Set("verificationUrl"),
            [EmailTemplateId.TwoFactorCode] = Set("otpCode", "expiryMinutes"),
            [EmailTemplateId.PasswordReset] = Set("resetUrl", "expiryMinutes"),
            [EmailTemplateId.PaymentFailed] = Set("paymentType", "amount", "currency", "failureReason", "billingUrl"),
            [EmailTemplateId.AutoRechargeSuccess] = Set("quantity", "amount", "currency", "newBalance", "billingUrl"),
            [EmailTemplateId.AutoRechargeFailed] = Set("quantity", "failureReason", "currentBalance", "billingUrl"),
            [EmailTemplateId.PriceChangeNotice] = Set("daysNotice", "quantity", "oldPrice", "newPrice", "currency", "cancelUrl", "billingUrl"),
        };

    public static IReadOnlyDictionary<string, string?> EmailVerification(string verificationUrl) =>
        new Dictionary<string, string?> { ["verificationUrl"] = verificationUrl };

    public static IReadOnlyDictionary<string, string?> TwoFactorCode(string otpCode, int expiryMinutes) =>
        new Dictionary<string, string?>
        {
            ["otpCode"] = otpCode,
            ["expiryMinutes"] = expiryMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    public static IReadOnlyDictionary<string, string?> PasswordReset(string resetUrl, int expiryMinutes) =>
        new Dictionary<string, string?>
        {
            ["resetUrl"] = resetUrl,
            ["expiryMinutes"] = expiryMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    public static IReadOnlyDictionary<string, string?> PaymentFailed(
        string paymentType, string amount, string currency, string failureReason, string billingUrl) =>
        new Dictionary<string, string?>
        {
            ["paymentType"] = paymentType,
            ["amount"] = amount,
            ["currency"] = currency,
            ["failureReason"] = failureReason,
            ["billingUrl"] = billingUrl,
        };

    public static IReadOnlyDictionary<string, string?> AutoRechargeSuccess(
        string quantity, string amount, string currency, string newBalance, string billingUrl) =>
        new Dictionary<string, string?>
        {
            ["quantity"] = quantity,
            ["amount"] = amount,
            ["currency"] = currency,
            ["newBalance"] = newBalance,
            ["billingUrl"] = billingUrl,
        };

    public static IReadOnlyDictionary<string, string?> AutoRechargeFailed(
        string quantity, string failureReason, string currentBalance, string billingUrl) =>
        new Dictionary<string, string?>
        {
            ["quantity"] = quantity,
            ["failureReason"] = failureReason,
            ["currentBalance"] = currentBalance,
            ["billingUrl"] = billingUrl,
        };

    public static IReadOnlyDictionary<string, string?> PriceChangeNotice(
        string daysNotice, string quantity, string oldPrice, string newPrice, string currency, string cancelUrl, string billingUrl) =>
        new Dictionary<string, string?>
        {
            ["daysNotice"] = daysNotice,
            ["quantity"] = quantity,
            ["oldPrice"] = oldPrice,
            ["newPrice"] = newPrice,
            ["currency"] = currency,
            ["cancelUrl"] = cancelUrl,
            ["billingUrl"] = billingUrl,
        };

    private static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);
}
