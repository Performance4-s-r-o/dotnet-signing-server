using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;

namespace DotNetSigningServer.Controllers;

public class AccountController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuthService _authService;
    private readonly IEmailSender _emailSender;
    private readonly AppOptions _appOptions;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _loginAttempts = new();
    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    public AccountController(ApplicationDbContext dbContext, IAuthService authService, IEmailSender emailSender, IOptions<AppOptions> appOptions, IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _authService = authService;
        _emailSender = emailSender;
        _appOptions = appOptions.Value;
        _localizer = localizer;
    }

    private bool IsRateLimited(string key)
    {
        var now = DateTime.UtcNow;
        var entry = _loginAttempts.AddOrUpdate(
            key,
            _ => (1, now),
            (_, existing) =>
            {
                if (now - existing.WindowStart > RateLimitWindow)
                {
                    return (1, now);
                }
                return (existing.Count + 1, existing.WindowStart);
            });
        return entry.Count > MaxAttemptsPerWindow;
    }

    [Authorize]
    [HttpGet("/Account")]
    public async Task<IActionResult> Index()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction(nameof(SignIn));
        }

        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(SignIn));
        }

        return View(user);
    }

    [HttpGet("/Account/SignUp")]
    public IActionResult SignUp()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        return View(new SignUpViewModel());
    }

    [HttpPost("/Account/SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(SignUpViewModel model)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (!ModelState.IsValid) return View(model);

        bool exists = await _dbContext.Users.AnyAsync(u => u.Email == model.Email);
        if (exists)
        {
            ModelState.AddModelError(string.Empty, _localizer["EmailAlreadyRegistered"]);
            return View(model);
        }

        var (hash, salt) = _authService.HashPassword(model.Password);
        var verificationToken = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = model.Email,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            EmailVerified = false,
            EmailVerificationToken = verificationToken,
            CreditsRemaining = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        // Send verification email (critical — user cannot complete signup without it)
        var verificationLink = BuildAbsoluteUrl($"/Account/Verify?token={Uri.EscapeDataString(verificationToken)}");
        var settingsUrl = BuildAbsoluteUrl("/Account/Settings");
        var subject = "Verify your email for P4PDF by Performance4 s.r.o.";
        var body = $@"<p>Hello,</p>
<p>Please verify your email to activate your account.</p>
<p><a href=""{verificationLink}"">Click here to verify</a></p>
<p>If the link does not work, copy and paste this URL into your browser:<br/>{verificationLink}</p>";

        try
        {
            await _emailSender.SendAsync(user.Email, subject, body, settingsUrl, isCritical: true);
            TempData["Info"] = _localizer["CheckEmailVerification"].Value;
        }
        catch
        {
            TempData["Error"] = _localizer["VerificationEmailFailed"].Value;
        }

        return RedirectToAction(nameof(Verify));
    }

    private string BuildAbsoluteUrl(string path)
    {
        var configuredHost = _appOptions.FqdnServerName?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            if (configuredHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                configuredHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return $"{configuredHost}{path}";
            }

            var scheme = Request?.IsHttps == true ? "https" : "http";
            return $"{scheme}://{configuredHost}{path}";
        }

        var fallbackScheme = Request?.Scheme ?? "https";
        var host = Request?.Host.Value ?? "localhost";
        return $"{fallbackScheme}://{host}{path}";
    }

    [HttpGet("/Account/SignIn")]
    public IActionResult SignIn(string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        ViewData["ReturnUrl"] = returnUrl;
        return View(new SignInViewModel());
    }

    [HttpPost("/Account/SignIn")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignIn(SignInViewModel model, string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (!ModelState.IsValid) return View(model);

        var rateLimitKey = $"signin:{(model.Email ?? "").ToLowerInvariant()}";
        if (IsRateLimited(rateLimitKey))
        {
            ModelState.AddModelError(string.Empty, _localizer["TooManySignInAttempts"]);
            return View(model);
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == model.Email);
        if (user == null || !_authService.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt))
        {
            ModelState.AddModelError(string.Empty, _localizer["InvalidCredentials"]);
            return View(model);
        }

        if (!user.IsActive)
        {
            ModelState.AddModelError("", _localizer["AccountDeactivated"]);
            return View(new SignInViewModel { Email = model.Email });
        }

        if (!user.EmailVerified)
        {
            ModelState.AddModelError(string.Empty, _localizer["VerifyEmailFirst"]);
            return View(model);
        }

        // Generate and send 2FA code
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.EmailOtpCode = otp;
        user.EmailOtpExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await _dbContext.SaveChangesAsync();

        var otpSettingsUrl = BuildAbsoluteUrl("/Account/Settings");
        var subject = "Your 2FA code";
        var body = $"<p>Your verification code is: <strong>{otp}</strong></p><p>This code expires in 10 minutes.</p>";
        try
        {
            // 2FA codes are critical security emails — always sent regardless of notification preferences
            await _emailSender.SendAsync(user.Email, subject, body, otpSettingsUrl, isCritical: true);
            TempData["Info"] = _localizer["TwoFactorCodeSent"].Value;
        }
        catch
        {
            TempData["Error"] = _localizer["TwoFactorCodeFailed"].Value;
            return View(model);
        }

        TempData["ReturnUrl"] = returnUrl;
        return RedirectToAction(nameof(TwoFactor), new { email = user.Email, rememberMe = model.RememberMe });
    }

    [Authorize]
    [HttpPost("/Account/SignOut")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutUser()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    [HttpGet("/Account/Denied")]
    public IActionResult Denied() => Content(_localizer["AccessDenied"].Value);

    [HttpGet("/Account/Verify")]
    public async Task<IActionResult> Verify(string? token = null)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
            if (user != null)
            {
                user.EmailVerified = true;
                user.EmailVerificationToken = null;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync();

                await SignInUser(user, rememberMe: false);
                TempData["Info"] = _localizer["EmailVerified"].Value;
                return RedirectToAction("Index", "Home", new { signup = "success" });
            }

            TempData["Error"] = _localizer["InvalidVerificationToken"].Value;
        }

        return View();
    }

    [HttpPost("/Account/Verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyPost(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = _localizer["VerificationTokenRequired"].Value;
            return RedirectToAction(nameof(Verify));
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
        if (user == null)
        {
            TempData["Error"] = _localizer["InvalidVerificationToken"].Value;
            return View("Verify");
        }

        user.EmailVerified = true;
        user.EmailVerificationToken = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        await SignInUser(user, rememberMe: false);
        TempData["Info"] = _localizer["EmailVerified"].Value;
        return RedirectToAction("Index", "Home", new { signup = "success" });
    }

    [HttpGet("/Account/TwoFactor")]
    public IActionResult TwoFactor(string email, bool rememberMe = false)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction(nameof(SignIn));
        }
        ViewData["Email"] = email;
        ViewData["RememberMe"] = rememberMe;
        return View();
    }

    [HttpPost("/Account/TwoFactor")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorPost(string email, string code, bool rememberMe = false)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = _localizer["EmailAndCodeRequired"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        var rateLimitKey = $"2fa:{email.ToLowerInvariant()}";
        if (IsRateLimited(rateLimitKey))
        {
            TempData["Error"] = _localizer["TooManyVerificationAttempts"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || user.EmailOtpCode == null || user.EmailOtpExpiresAt == null)
        {
            TempData["Error"] = _localizer["VerificationCodeInvalid"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        if (!user.IsActive)
        {
            TempData["Error"] = _localizer["AccountDeactivated"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        if (user.EmailOtpExpiresAt < DateTimeOffset.UtcNow || !string.Equals(user.EmailOtpCode, code.Trim(), StringComparison.Ordinal))
        {
            TempData["Error"] = _localizer["VerificationCodeInvalid"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        // Clear OTP and sign in
        user.EmailOtpCode = null;
        user.EmailOtpExpiresAt = null;
        await _dbContext.SaveChangesAsync();

        await SignInUser(user, rememberMe);
        var returnUrl = TempData["ReturnUrl"]?.ToString();
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    [HttpGet("/Account/ForgotPassword")]
    public IActionResult ForgotPassword()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        return View(new ForgotPasswordViewModel());
    }

    [HttpPost("/Account/ForgotPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (!ModelState.IsValid) return View(model);

        var rateLimitKey = $"forgot:{(model.Email ?? "").ToLowerInvariant()}";
        if (IsRateLimited(rateLimitKey))
        {
            // Always show success to prevent email enumeration
            TempData["Info"] = _localizer["PasswordResetEmailSent"].Value;
            return View(new ForgotPasswordViewModel());
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == model.Email);
        if (user != null && user.IsActive && user.EmailVerified)
        {
            var token = Guid.NewGuid().ToString("N");
            user.PasswordResetToken = token;
            user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();

            var resetLink = BuildAbsoluteUrl($"/Account/ResetPassword?token={Uri.EscapeDataString(token)}");
            var settingsUrl = BuildAbsoluteUrl("/Account/Settings");
            var subject = "Reset your P4PDF password";
            var body = $@"<p>Hello,</p>
<p>We received a request to reset your password.</p>
<p><a href=""{resetLink}"">Click here to reset your password</a></p>
<p>If the link does not work, copy and paste this URL into your browser:<br/>{resetLink}</p>
<p>This link expires in 1 hour. If you did not request a password reset, you can safely ignore this email.</p>";

            try
            {
                await _emailSender.SendAsync(user.Email, subject, body, settingsUrl, isCritical: true);
            }
            catch
            {
                // Log but don't reveal failure to prevent enumeration
            }
        }

        // Always show success message to prevent email enumeration
        TempData["Info"] = _localizer["PasswordResetEmailSent"].Value;
        return View(new ForgotPasswordViewModel());
    }

    [HttpGet("/Account/ResetPassword")]
    public IActionResult ResetPassword(string? token = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = _localizer["InvalidResetToken"].Value;
            return RedirectToAction(nameof(ForgotPassword));
        }
        return View(new ResetPasswordViewModel { Token = token });
    }

    [HttpPost("/Account/ResetPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        if (!ModelState.IsValid) return View(model);

        var user = await _dbContext.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == model.Token &&
            u.PasswordResetExpiresAt != null &&
            u.PasswordResetExpiresAt > DateTimeOffset.UtcNow);

        if (user == null)
        {
            TempData["Error"] = _localizer["InvalidResetToken"].Value;
            return RedirectToAction(nameof(ForgotPassword));
        }

        var (hash, salt) = _authService.HashPassword(model.Password);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordResetToken = null;
        user.PasswordResetExpiresAt = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        TempData["Info"] = _localizer["PasswordResetSuccess"].Value;
        return RedirectToAction(nameof(SignIn));
    }

    [Authorize]
    [HttpGet("/Account/Settings")]
    public async Task<IActionResult> Settings()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction(nameof(SignIn));

        return View(user);
    }

    [Authorize]
    [HttpPost("/Account/Settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(bool emailNotificationsEnabled)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction(nameof(SignIn));

        user.EmailNotificationsEnabled = emailNotificationsEnabled;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        TempData["Info"] = _localizer["SettingsSaved"].Value;
        return RedirectToAction(nameof(Settings));
    }

    private async Task SignInUser(User user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                AllowRefresh = true
            });
    }

    /// <summary>
    /// Sends an email respecting the user's notification preference.
    /// Critical emails (2FA, verification) are always sent regardless of the preference.
    /// </summary>
    private async Task<bool> SendEmailIfAllowed(Models.User user, string subject, string htmlBody, bool isCritical)
    {
        if (!isCritical && !user.EmailNotificationsEnabled)
        {
            return false;
        }

        var settingsUrl = BuildAbsoluteUrl("/Account/Settings");
        await _emailSender.SendAsync(user.Email, subject, htmlBody, settingsUrl, isCritical);
        return true;
    }

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
