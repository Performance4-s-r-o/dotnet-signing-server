using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;

namespace DotNetSigningServer.Controllers;

public class AccountController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuthService _authService;
    private readonly IEmailSender _emailSender;

    public AccountController(ApplicationDbContext dbContext, IAuthService authService, IEmailSender emailSender)
    {
        _dbContext = dbContext;
        _authService = authService;
        _emailSender = emailSender;
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
    public IActionResult SignUp() => View(new SignUpViewModel());

    [HttpPost("/Account/SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(SignUpViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        bool exists = await _dbContext.Users.AnyAsync(u => u.Email == model.Email);
        if (exists)
        {
            ModelState.AddModelError(string.Empty, "Email already registered.");
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

        // Send verification email
        var verificationLink = Url.Action("Verify", "Account", new { token = verificationToken }, Request.Scheme) ?? string.Empty;
        var subject = "Verify your email for DotNet Signing Server";
        var body = $@"<p>Hello,</p>
<p>Please verify your email to activate your account.</p>
<p><a href=""{verificationLink}"">Click here to verify</a></p>
<p>If the link does not work, copy and paste this URL into your browser:<br/>{verificationLink}</p>";

        try
        {
            await _emailSender.SendAsync(user.Email, subject, body);
            TempData["Info"] = "Please check your email for a verification link.";
        }
        catch
        {
            TempData["Error"] = "We could not send a verification email. Please contact support or try again later.";
        }

        return RedirectToAction(nameof(Verify));
    }

    [HttpGet("/Account/SignIn")]
    public IActionResult SignIn(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View(new SignInViewModel());
    }

    [HttpPost("/Account/SignIn")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignIn(SignInViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == model.Email);
        if (user == null || !_authService.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt))
        {
            ModelState.AddModelError(string.Empty, "Invalid credentials.");
            return View(model);
        }

        if (!user.EmailVerified)
        {
            ModelState.AddModelError(string.Empty, "Please verify your email before signing in.");
            return View(model);
        }

        // Generate and send 2FA code
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.EmailOtpCode = otp;
        user.EmailOtpExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await _dbContext.SaveChangesAsync();

        var subject = "Your 2FA code";
        var body = $"<p>Your verification code is: <strong>{otp}</strong></p><p>This code expires in 10 minutes.</p>";
        try
        {
            await _emailSender.SendAsync(user.Email, subject, body);
            TempData["Info"] = "We sent a 6-digit code to your email.";
        }
        catch
        {
            TempData["Error"] = "We could not send the verification code. Please try again.";
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
    public IActionResult Denied() => Content("Access denied");

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
                TempData["Info"] = "Email verified. You are now signed in.";
                return RedirectToAction("Index", "Home", new { signup = "success" });
            }

            TempData["Error"] = "Invalid or expired verification token.";
        }

        return View();
    }

    [HttpPost("/Account/Verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyPost(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = "Verification token is required.";
            return RedirectToAction(nameof(Verify));
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
        if (user == null)
        {
            TempData["Error"] = "Invalid or expired verification token.";
            return View("Verify");
        }

        user.EmailVerified = true;
        user.EmailVerificationToken = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        await SignInUser(user, rememberMe: false);
        TempData["Info"] = "Email verified. You are now signed in.";
        return RedirectToAction("Index", "Home", new { signup = "success" });
    }

    [HttpGet("/Account/TwoFactor")]
    public IActionResult TwoFactor(string email, bool rememberMe = false)
    {
        ViewData["Email"] = email;
        ViewData["RememberMe"] = rememberMe;
        return View();
    }

    [HttpPost("/Account/TwoFactor")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorPost(string email, string code, bool rememberMe = false)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = "Email and code are required.";
            return RedirectToAction(nameof(SignIn));
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || user.EmailOtpCode == null || user.EmailOtpExpiresAt == null)
        {
            TempData["Error"] = "Verification code is invalid or expired.";
            return RedirectToAction(nameof(SignIn));
        }

        if (user.EmailOtpExpiresAt < DateTimeOffset.UtcNow || !string.Equals(user.EmailOtpCode, code.Trim(), StringComparison.Ordinal))
        {
            TempData["Error"] = "Verification code is invalid or expired.";
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

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
