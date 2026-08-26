using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
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
            // Do not reveal that the address is already registered — that turns signup
            // into an account-enumeration oracle. Show the same "check your inbox"
            // outcome as a fresh signup; the real owner already has an account and can
            // sign in or use the password-reset flow.
            TempData["Info"] = _localizer["CheckEmailVerification"].Value;
            return RedirectToAction(nameof(Verify));
        }

        var (hash, salt, iterations) = _authService.HashPassword(model.Password);
        var verificationToken = SecureTokens.Generate();
        var user = new User
        {
            Email = model.Email,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            IsActive = true,
            EmailVerified = false,
            // Only the hash is persisted — a DB dump must not yield usable
            // account-takeover links.
            EmailVerificationToken = SecureTokens.Hash(verificationToken),
            EmailVerificationExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
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

    private string BuildAbsoluteUrl(string path) => _appOptions.AbsoluteUrl(path);

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
        if (user == null || !_authService.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            ModelState.AddModelError(string.Empty, _localizer["InvalidCredentials"]);
            return View(model);
        }

        // Deactivated accounts get the generic message: a distinct one tells an
        // attacker the address exists and the password is right.
        if (!user.IsActive)
        {
            ModelState.AddModelError(string.Empty, _localizer["InvalidCredentials"]);
            return View(new SignInViewModel { Email = model.Email });
        }

        // Reaching this point already required the correct password, so naming the
        // unverified state leaks nothing the caller doesn't have.
        if (!user.EmailVerified)
        {
            ModelState.AddModelError(string.Empty, _localizer["VerifyEmailFirst"]);
            return View(model);
        }

        // Upgrade hashes made with an older iteration count now that we hold the
        // plaintext password.
        if (user.PasswordIterations != _authService.Iterations)
        {
            var (upgradedHash, upgradedSalt, upgradedIterations) = _authService.HashPassword(model.Password);
            user.PasswordHash = upgradedHash;
            user.PasswordSalt = upgradedSalt;
            user.PasswordIterations = upgradedIterations;
            // Deliberately not touching UpdatedAt — it doubles as the session security
            // stamp, and a rehash must not sign the user's other sessions out.
        }

        // Generate and send 2FA code
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        // Stored hashed, like every other single-use secret here: reading the
        // database must not be enough to walk through someone's second factor.
        user.EmailOtpCode = SecureTokens.Hash(otp);
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

    // GET is deliberately read-only. Verifying and signing in straight from the link
    // is login-CSRF: an attacker sends their own verification link to a victim, and
    // one visit silently swaps the victim's session into the attacker's account, so
    // everything the victim uploads or saves afterwards lands there. The link now
    // just renders an antiforgery-protected confirm form.
    [HttpGet("/Account/Verify")]
    public IActionResult Verify(string? token = null)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            ViewData["Token"] = token;
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

        var now = DateTimeOffset.UtcNow;
        var tokenHash = SecureTokens.Hash(token);
        var user = await _dbContext.Users.FirstOrDefaultAsync(u =>
            u.EmailVerificationToken == tokenHash &&
            u.EmailVerificationExpiresAt != null &&
            u.EmailVerificationExpiresAt > now);

        if (user == null)
        {
            TempData["Error"] = _localizer["InvalidVerificationToken"].Value;
            return View("Verify");
        }

        user.EmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationExpiresAt = null;
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

        if (user.EmailOtpExpiresAt < DateTimeOffset.UtcNow || !SecureTokens.MatchesHash(code, user.EmailOtpCode))
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
            var token = SecureTokens.Generate();
            user.PasswordResetToken = SecureTokens.Hash(token);
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

        var now = DateTimeOffset.UtcNow;
        var tokenHash = SecureTokens.Hash(model.Token ?? string.Empty);
        var user = await _dbContext.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == tokenHash &&
            u.PasswordResetExpiresAt != null &&
            u.PasswordResetExpiresAt > now);

        if (user == null)
        {
            TempData["Error"] = _localizer["InvalidResetToken"].Value;
            return RedirectToAction(nameof(ForgotPassword));
        }

        var (hash, salt, iterations) = _authService.HashPassword(model.Password);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
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

    [Authorize]
    [HttpPost("/Account/Settings/MaxConcurrent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMaxConcurrent(int? maxConcurrent)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction(nameof(SignIn));

        // Clamp to a sane range; NULL = use plan default
        if (maxConcurrent.HasValue)
        {
            if (maxConcurrent.Value < 1 || maxConcurrent.Value > 50)
            {
                TempData["Error"] = _localizer["InvalidConcurrencyLimit"].Value;
                return RedirectToAction(nameof(Settings));
            }
        }

        user.MaxConcurrentOperations = maxConcurrent;
        // Intentionally NOT touching UpdatedAt — it's used as the cookie security stamp
        // (see Program.cs cookie OnValidatePrincipal). Bumping it here would sign the user out.
        await _dbContext.SaveChangesAsync();
        UserConcurrencyMiddleware.InvalidateLimitCache(user.Id);

        TempData["Info"] = _localizer["ConcurrencyLimitUpdated"].Value;
        return RedirectToAction(nameof(Settings));
    }

    [Authorize]
    [HttpPost("/Account/Settings/QueueTimeout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQueueTimeout(int? queueTimeoutSeconds)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction(nameof(SignIn));

        user.ConcurrencyQueueTimeoutSeconds = queueTimeoutSeconds < 0 ? null : queueTimeoutSeconds;
        await _dbContext.SaveChangesAsync();
        UserConcurrencyMiddleware.InvalidateLimitCache(user.Id);

        TempData["Info"] = _localizer["ConcurrencyQueueTimeoutUpdated"].Value;
        return RedirectToAction(nameof(Settings));
    }

    [Authorize]
    [HttpGet("/Account/ChangePassword")]
    public async Task<IActionResult> ChangePassword()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(SignIn));
        }

        return View(new ChangePasswordViewModel());
    }

    [Authorize]
    [HttpPost("/Account/ChangePassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        if (!ModelState.IsValid) return View(model);

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(SignIn));
        }

        var rateLimitKey = $"changepw:{user.Id}";
        if (IsRateLimited(rateLimitKey))
        {
            ModelState.AddModelError(string.Empty, _localizer["TooManySignInAttempts"]);
            return View(model);
        }

        if (!_authService.VerifyPassword(model.CurrentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), _localizer["CurrentPasswordIncorrect"]);
            return View(model);
        }

        if (_authService.VerifyPassword(model.NewPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            ModelState.AddModelError(nameof(model.NewPassword), _localizer["NewPasswordSameAsCurrent"]);
            return View(model);
        }

        var (hash, salt, iterations) = _authService.HashPassword(model.NewPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        // Re-issue cookie so SecurityStamp matches the new UpdatedAt
        await SignInUser(user, rememberMe: false);

        TempData["Info"] = _localizer["PasswordChangedSuccess"].Value;
        return RedirectToAction(nameof(Index));
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
