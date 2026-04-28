using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using System.Globalization;
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
    private readonly IEmailTemplateRenderer _emailTemplates;
    private readonly AppOptions _appOptions;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _loginAttempts = new();
    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private static DateTime _lastCleanup = DateTime.UtcNow;

    public AccountController(
        ApplicationDbContext dbContext,
        IAuthService authService,
        IEmailSender emailSender,
        IEmailTemplateRenderer emailTemplates,
        IOptions<AppOptions> appOptions,
        IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _authService = authService;
        _emailSender = emailSender;
        _emailTemplates = emailTemplates;
        _appOptions = appOptions.Value;
        _localizer = localizer;
    }

    private string CurrentLocale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    private bool IsRateLimited(string key)
    {
        var now = DateTime.UtcNow;

        // Periodic cleanup of stale entries to prevent memory leak
        if (now - _lastCleanup > TimeSpan.FromMinutes(5))
        {
            _lastCleanup = now;
            var staleKeys = _loginAttempts
                .Where(kv => now - kv.Value.WindowStart > TimeSpan.FromMinutes(10))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var staleKey in staleKeys)
            {
                _loginAttempts.TryRemove(staleKey, out _);
            }
        }

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
        var rendered = _emailTemplates.Render(EmailTemplateId.EmailVerification, CurrentLocale, new Dictionary<string, string?>
        {
            ["verificationUrl"] = verificationLink,
        });

        try
        {
            await _emailSender.SendAsync(user.Email, rendered.Subject, rendered.HtmlBody);
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

        var rendered = _emailTemplates.Render(EmailTemplateId.TwoFactorCode, CurrentLocale, new Dictionary<string, string?>
        {
            ["otpCode"] = otp,
            ["expiryMinutes"] = "10",
        });
        try
        {
            await _emailSender.SendAsync(user.Email, rendered.Subject, rendered.HtmlBody);
            TempData["Info"] = _localizer["TwoFactorCodeSent"].Value;
        }
        catch
        {
            TempData["Error"] = _localizer["TwoFactorCodeFailed"].Value;
            return View(model);
        }

        TempData["ReturnUrl"] = returnUrl;
        TempData["2FA_Email"] = user.Email;
        TempData["2FA_RememberMe"] = model.RememberMe.ToString();
        return RedirectToAction(nameof(TwoFactor));
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
    public IActionResult TwoFactor()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        var email = TempData["2FA_Email"] as string;
        var rememberMe = bool.TryParse(TempData["2FA_RememberMe"] as string, out var rm) && rm;
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction(nameof(SignIn));
        }
        // Keep TempData alive for the POST handler
        TempData["2FA_Email"] = email;
        TempData["2FA_RememberMe"] = rememberMe.ToString();
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
            var rendered = _emailTemplates.Render(EmailTemplateId.PasswordReset, CurrentLocale, new Dictionary<string, string?>
            {
                ["resetUrl"] = resetLink,
                ["expiryMinutes"] = "60",
            });

            try
            {
                await _emailSender.SendAsync(user.Email, rendered.Subject, rendered.HtmlBody);
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
            new Claim(ClaimTypes.Name, user.Email),
            new Claim("SecurityStamp", user.UpdatedAt.Ticks.ToString())
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

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

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
