namespace DotNetSigningServer.Services.Email;

public static class EmailTemplateId
{
    public const string EmailVerification = "email_verification";
    public const string TwoFactorCode = "two_factor_code";
    public const string PasswordReset = "password_reset";
    public const string PaymentFailed = "payment_failed";
    public const string AutoRechargeFailed = "auto_recharge_failed";
    public const string AutoRechargeSuccess = "auto_recharge_success";
    public const string PriceChangeNotice = "price_change_notice";
}
