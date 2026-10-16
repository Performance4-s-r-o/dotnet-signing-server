using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Services.Consents;
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
    private readonly ITemplatedEmailSender _email;
    private readonly AppOptions _appOptions;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    private readonly AuthAttemptLimiter _limiter;
    private readonly SessionSecurityOptions _security;

    /// <summary>
    /// HttpOnly cookie with the sign-in challenge (#80): the 2FA step is bound to the browser that
    /// passed the password step, not to an address in the form.
    /// </summary>
    public const string LoginChallengeCookie = "p4pdf.2fa";
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);

    public AccountController(
        ApplicationDbContext dbContext,
        IAuthService authService,
        ITemplatedEmailSender email,
        IOptions<AppOptions> appOptions,
        IStringLocalizer<SharedStrings> localizer,
        AuthAttemptLimiter limiter,
        IOptions<SessionSecurityOptions> security)
    {
        _dbContext = dbContext;
        _authService = authService;
        _email = email;
        _appOptions = appOptions.Value;
        _localizer = localizer;
        _limiter = limiter;
        _security = security.Value;
    }

    private string CurrentLocale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary><see cref="CurrentLocale"/> as stored in <c>User.Locale</c> (for e-mails sent later).</summary>
    private string StoredLocale
    {
        get
        {
            var locale = CurrentLocale.ToLowerInvariant();
            return locale.Length > Models.User.MaxLocaleLength ? locale[..Models.User.MaxLocaleLength] : locale;
        }
    }

    /// <summary>
    /// Shared across instances (#80). Keys that start from an address also carry the client IP:
    /// keyed by the address alone, anybody who knew it could keep its owner locked out.
    /// </summary>
    private Task<bool> IsRateLimitedAsync(string key) => _limiter.IsLimitedAsync(key, cancellationToken: HttpContext.RequestAborted);

    /// <summary>
    /// Second limit, by account alone (#80 review): the IP in the first key comes from a header
    /// (<c>ClientIp.Of</c>), and a fresh IP must not mean a fresh supply of password or code
    /// guesses. Wider than the per-IP window so the owner is not locked out by a few typos.
    /// </summary>
    private Task<bool> IsAccountRateLimitedAsync(string key) => _limiter.IsLimitedAsync(
        key, _security.AccountMaxAttempts, _security.AccountWindow, HttpContext.RequestAborted);

    private string RequestIp() => DotNetSigningServer.Middleware.ClientIp.Of(HttpContext);

    [CookieAuthorize]
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
    public async Task<IActionResult> SignUp(
        [FromServices] ConsentDocumentResolver consentDocuments,
        [FromServices] IOptions<P4BackofficeProductOptions> backoffice,
        [FromServices] ConsentPromptsCache? prompts = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }
        var model = new SignUpViewModel();
        await PrepareSignUpFormAsync(model, consentDocuments, backoffice.Value, prompts: prompts);
        return View(model);
    }

    [HttpPost("/Account/SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(
        SignUpViewModel model,
        [FromServices] ConsentDocumentResolver consentDocuments,
        [FromServices] ConsentService consents,
        [FromServices] IOptions<P4BackofficeProductOptions> backoffice,
        [FromServices] TimeProvider time,
        [FromServices] ILogger<AccountController> logger,
        [FromServices] ConsentPromptsCache? prompts = null)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        // Checked here too: a bypassed HTML "required" must not create an account without consent.
        if (!model.AcceptTerms)
        {
            ModelState.Remove(nameof(SignUpViewModel.AcceptTerms));
            ModelState.AddModelError(nameof(SignUpViewModel.AcceptTerms), _localizer["SignUpAcceptTermsRequired"].Value);
        }

        // Local data only (docs:meta, LegalDocuments): never the service.
        var shownDocuments = await consentDocuments.ResolveAsync(
            ConsentRequirements.From(backoffice.Value.Consents), CurrentLocale, HttpContext.RequestAborted);
        if (!ModelState.IsValid)
        {
            await PrepareSignUpFormAsync(model, consentDocuments, backoffice.Value, shownDocuments, prompts);
            return View(model);
        }

        var consentsOn = backoffice.Value.ModeFor(BackofficeModule.Consents) != BackofficeMode.Off;
        var accepted = consentsOn
            ? ConsentVersionCheck.Accept(model.ShownVersions, shownDocuments, time.GetUtcNow())
            : shownDocuments.ToDictionary(d => d.Document, d => d.Version, StringComparer.Ordinal);
        if (accepted is null)
        {
            // A new version came into force more than 10 minutes after the form was opened.
            ModelState.AddModelError(string.Empty, _localizer["LegalVersionOutdated"].Value);
            await PrepareSignUpFormAsync(model, consentDocuments, backoffice.Value, shownDocuments, prompts);
            return View(model);
        }

        bool exists = await _dbContext.Users.AnyWithEmailAsync(model.Email);
        if (exists)
        {
            // Do not reveal that the address is already registered — that turns signup
            // into an account-enumeration oracle. Show the same "check your inbox"
            // outcome as a fresh signup; the real owner already has an account and can
            // sign in or use the password-reset flow. No consent is recorded: the
            // visitor is not (provably) the account's owner.
            //
            // Logged because the visitor's view is deliberately identical to a successful
            // signup: without this line a signup that sends nothing cannot be told from one
            // whose mail was lost, and the address stays out of the log for the same reason
            // it stays out of the response.
            logger.LogInformation("Sign-up for an address that already has an account; no verification e-mail sent.");
            TempData["Info"] = _localizer["CheckEmailVerification"].Value;
            return RedirectToAction(nameof(Verify));
        }

        var (hash, salt, iterations) = _authService.HashPassword(model.Password);
        var verificationToken = SecureTokens.Generate();
        var user = new User
        {
            // Trimmed; one account per address whatever the case is the lookup's and the
            // unique index's job (lower("Email"), #82).
            Email = UserEmails.Trim(model.Email),
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
            Locale = StoredLocale,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var choices = await ConsentChoicesAsync(shownDocuments, accepted, model.ShownHashes, consentDocuments, logger);
        _dbContext.Users.Add(user);
        // Records (and, unless the Consents module is Off, the outbox item) go into the same
        // SaveChangesAsync as the user: one transaction, nothing sent during the request.
        consents.RecordSignupConsents(
            user, choices, Request.Headers.UserAgent.ToString(), ClientIp(), PromptRefFromForm(prompts, model.PromptKey, model.PromptVersion, model.PromptHash));

        // Verification email (critical — user cannot complete signup without it). With the
        // Email module On it is queued in the same SaveChangesAsync as the user; otherwise it
        // is sent right after, as before.
        var verificationLink = BuildAbsoluteUrl($"/Account/Verify?token={Uri.EscapeDataString(verificationToken)}");
        var variables = EmailTemplateVariables.EmailVerification(verificationLink);
        var emailOptions = new EmailSendOptions(EmailTemplateId.EmailVerification, user.Locale, user.Id);
        var queued = _email.TryEnqueue(EmailTemplateId.EmailVerification, user.Email, CurrentLocale, variables, emailOptions);
        await _dbContext.SaveChangesAsync();

        if (queued)
        {
            TempData["Info"] = _localizer["CheckEmailVerification"].Value;
            return RedirectToAction(nameof(Verify));
        }

        try
        {
            await _email.SendAsync(EmailTemplateId.EmailVerification, user.Email, CurrentLocale, variables, emailOptions);
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

        var rateLimitKey = $"signin:{RequestIp()}:{UserEmails.Key(model.Email)}";
        if (await IsRateLimitedAsync(rateLimitKey)
            | await IsAccountRateLimitedAsync($"signin-account:{UserEmails.Key(model.Email)}"))
        {
            ModelState.AddModelError(string.Empty, _localizer["TooManySignInAttempts"]);
            return View(model);
        }

        var user = await _dbContext.Users.FindByEmailAsync(model.Email);
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
        user.EmailOtpExpiresAt = DateTimeOffset.UtcNow.Add(OtpLifetime);
        // EmailOtpFailedAttempts is NOT reset here (#80 review): with the password, a new sign-in
        // would otherwise hand out a fresh set of guesses. Only success or a reset clears it.
        // The code belongs to this sign-in only (#80): the browser gets the challenge, the
        // database its hash. A new sign-in replaces both.
        var challenge = SecureTokens.Generate();
        user.LoginChallengeHash = SecureTokens.Hash(challenge);
        // E-mails sent later (auto-recharge, payments) follow the language of the last sign-in.
        user.Locale = StoredLocale;

        var variables = EmailTemplateVariables.TwoFactorCode(otp, expiryMinutes: 10);
        var emailOptions = new EmailSendOptions(EmailTemplateId.TwoFactorCode, user.Locale, user.Id);
        // Email module On: queued with the code in one SaveChangesAsync (break-glass delivers it
        // if the service does not). Otherwise sent right after the save, as before.
        var queued = _email.TryEnqueue(EmailTemplateId.TwoFactorCode, user.Email, CurrentLocale, variables, emailOptions);
        await _dbContext.SaveChangesAsync();

        if (queued)
        {
            TempData["Info"] = _localizer["TwoFactorCodeSent"].Value;
        }
        else
        {
            try
            {
                await _email.SendAsync(EmailTemplateId.TwoFactorCode, user.Email, CurrentLocale, variables, emailOptions);
                TempData["Info"] = _localizer["TwoFactorCodeSent"].Value;
            }
            catch
            {
                TempData["Error"] = _localizer["TwoFactorCodeFailed"].Value;
                return View(model);
            }
        }

        Response.Cookies.Append(LoginChallengeCookie, challenge, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = OtpLifetime,
            IsEssential = true,
        });
        TempData["ReturnUrl"] = returnUrl;
        TempData["2FA_Email"] = user.Email;
        TempData["2FA_RememberMe"] = model.RememberMe.ToString();
        return RedirectToAction(nameof(TwoFactor));
    }

    [CookieAuthorize]
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
    public async Task<IActionResult> TwoFactorPost(string code, bool rememberMe = false)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        // The account comes from the challenge cookie of the password step, never from the
        // form (#80): a code is worth nothing to a browser that did not pass the password.
        var challenge = Request.Cookies[LoginChallengeCookie];
        if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = _localizer[string.IsNullOrWhiteSpace(challenge) ? "VerificationCodeInvalid" : "EmailAndCodeRequired"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        var challengeHash = SecureTokens.Hash(challenge);
        if (await IsRateLimitedAsync($"2fa:{challengeHash}"))
        {
            TempData["Error"] = _localizer["TooManyVerificationAttempts"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.LoginChallengeHash == challengeHash);
        if (user == null || user.EmailOtpCode == null || user.EmailOtpExpiresAt == null)
        {
            Response.Cookies.Delete(LoginChallengeCookie);
            TempData["Error"] = _localizer["VerificationCodeInvalid"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        // Per account, across every challenge a password holder can start (#80 review).
        if (await IsAccountRateLimitedAsync($"2fa-account:{user.Id}"))
        {
            TempData["Error"] = _localizer["TooManyVerificationAttempts"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        if (!user.IsActive)
        {
            ClearOtp(user);
            await _dbContext.SaveChangesAsync();
            Response.Cookies.Delete(LoginChallengeCookie);
            TempData["Error"] = _localizer["AccountDeactivated"].Value;
            return RedirectToAction(nameof(SignIn));
        }

        if (!await CheckOtpAsync(user, code))
        {
            if (user.EmailOtpCode == null)
            {
                // Void after too many wrong codes: the next try starts at the password again.
                Response.Cookies.Delete(LoginChallengeCookie);
                TempData["Error"] = _localizer["VerificationCodeVoided"].Value;
                return RedirectToAction(nameof(SignIn));
            }
            TempData["Error"] = _localizer["VerificationCodeInvalid"].Value;
            TempData["2FA_Email"] = user.Email;
            TempData["2FA_RememberMe"] = rememberMe.ToString();
            return RedirectToAction(nameof(TwoFactor));
        }

        ClearOtp(user);
        await _dbContext.SaveChangesAsync();
        Response.Cookies.Delete(LoginChallengeCookie);

        await SignInUser(user, rememberMe);
        var returnUrl = TempData["ReturnUrl"]?.ToString();
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    /// <summary>
    /// Checks an e-mailed code against the user's current one. A wrong code is counted and at
    /// <see cref="SessionSecurityOptions.OtpMaxFailedAttempts"/> the code is void (#80) —
    /// saved here, so the count survives whatever the caller does next.
    /// </summary>
    private async Task<bool> CheckOtpAsync(User user, string code)
    {
        if (user.EmailOtpCode == null || user.EmailOtpExpiresAt == null) return false;
        if (user.EmailOtpExpiresAt >= DateTimeOffset.UtcNow && SecureTokens.MatchesHash(code.Trim(), user.EmailOtpCode))
        {
            return true;
        }

        user.EmailOtpFailedAttempts++;
        if (user.EmailOtpFailedAttempts >= Math.Max(1, _security.OtpMaxFailedAttempts)
            || user.EmailOtpExpiresAt < DateTimeOffset.UtcNow)
        {
            // Void, but the count stays: the next code starts where this one ended.
            ClearOtp(user, resetFailures: false);
        }
        await _dbContext.SaveChangesAsync();
        return false;
    }

    /// <summary>Drops the waiting code. The failure count is cleared only by success or a password reset.</summary>
    private static void ClearOtp(User user, bool resetFailures = true)
    {
        user.EmailOtpCode = null;
        user.EmailOtpExpiresAt = null;
        if (resetFailures) user.EmailOtpFailedAttempts = 0;
        user.LoginChallengeHash = null;
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

        var rateLimitKey = $"forgot:{RequestIp()}:{UserEmails.Key(model.Email)}";
        if (await IsRateLimitedAsync(rateLimitKey))
        {
            // Always show success to prevent email enumeration
            TempData["Info"] = _localizer["PasswordResetEmailSent"].Value;
            return View(new ForgotPasswordViewModel());
        }

        var user = await _dbContext.Users.FindByEmailAsync(model.Email);
        if (user != null && user.IsActive && user.EmailVerified)
        {
            var token = SecureTokens.Generate();
            user.PasswordResetToken = SecureTokens.Hash(token);
            user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            // UpdatedAt stays (#80): it is the session stamp, and asking for a reset link — which
            // anybody who knows the address can do — must not sign the owner out. The completed
            // reset moves it.

            var resetLink = BuildAbsoluteUrl($"/Account/ResetPassword?token={Uri.EscapeDataString(token)}");
            var variables = EmailTemplateVariables.PasswordReset(resetLink, expiryMinutes: 60);
            var emailOptions = new EmailSendOptions(EmailTemplateId.PasswordReset, StoredLocale, user.Id);
            // Email module On: queued with the token in one SaveChangesAsync; otherwise sent after it.
            var queued = _email.TryEnqueue(EmailTemplateId.PasswordReset, user.Email, CurrentLocale, variables, emailOptions);
            await _dbContext.SaveChangesAsync();

            if (!queued)
            {
                try
                {
                    await _email.SendAsync(EmailTemplateId.PasswordReset, user.Email, CurrentLocale, variables, emailOptions);
                }
                catch
                {
                    // Log but don't reveal failure to prevent enumeration
                }
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
        // A reset is what someone does when the account may be in other hands (#80): a waiting
        // code goes, and so do browser tokens, which live in web pages. Server tokens stay — the
        // integrations behind them (the portal's among them) would stop with no one to notice.
        ClearOtp(user);
        var now2 = DateTimeOffset.UtcNow;
        var revoked = await _dbContext.ApiTokens
            .Where(t => t.UserId == user.Id && t.IsBrowserToken && t.RevokedAt == null)
            .ToListAsync();
        foreach (var token in revoked) token.RevokedAt = now2;
        await _dbContext.SaveChangesAsync();
        var apiAuth = HttpContext.RequestServices?.GetService<IApiAuthService>();
        foreach (var token in revoked) apiAuth?.InvalidateTokenCache(token.Id);

        TempData["Info"] = _localizer["PasswordResetSuccess"].Value;
        return RedirectToAction(nameof(SignIn));
    }

    [CookieAuthorize]
    [HttpGet("/Account/Settings")]
    public async Task<IActionResult> Settings()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));

        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction(nameof(SignIn));

        return View(user);
    }

    [CookieAuthorize]
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

    [CookieAuthorize]
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

    [CookieAuthorize]
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

    [CookieAuthorize]
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

    [CookieAuthorize]
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
        if (await IsRateLimitedAsync(rateLimitKey))
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
        // auth_time = now: signing in is a fresh confirmation for step-up actions (#80).
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionClaims.CookiePrincipal(user, DateTimeOffset.UtcNow),
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                AllowRefresh = true
            });
    }

    // ── Step-up re-authentication (#80) ─────────────────────────────────────────────
    // Creating an API token and changing billing need a password or e-mailed code confirmed
    // within Auth:ReauthMinutes (RequireRecentAuthAttribute). These pages renew it.

    [CookieAuthorize]
    [HttpGet(Filters.RequireRecentAuthAttribute.ReauthPath)]
    public IActionResult Reauth(string? returnUrl = null, bool codeSent = false) =>
        View(new ReauthViewModel { ReturnUrl = SafeReturnUrl(returnUrl), CodeSent = codeSent });

    [CookieAuthorize]
    [HttpPost(Filters.RequireRecentAuthAttribute.ReauthPath)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reauth(ReauthViewModel model)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null || !user.IsActive) return RedirectToAction(nameof(SignIn));

        model.ReturnUrl = SafeReturnUrl(model.ReturnUrl);
        if (await IsRateLimitedAsync($"reauth:{user.Id}"))
        {
            ModelState.AddModelError(string.Empty, _localizer["TooManySignInAttempts"].Value);
            return View(model);
        }

        bool confirmed;
        if (!string.IsNullOrWhiteSpace(model.Code))
        {
            // Only a code sent for re-auth: a sign-in code carries a challenge of its own.
            confirmed = user.LoginChallengeHash == null && await CheckOtpAsync(user, model.Code);
            if (confirmed)
            {
                ClearOtp(user);
                await _dbContext.SaveChangesAsync();
            }
            else if (user.EmailOtpCode == null)
            {
                model.CodeSent = false;
            }
        }
        else
        {
            confirmed = !string.IsNullOrEmpty(model.Password)
                && _authService.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations);
        }

        if (!confirmed)
        {
            ModelState.AddModelError(string.Empty, _localizer[string.IsNullOrWhiteSpace(model.Code) ? "ReauthPasswordInvalid" : "VerificationCodeInvalid"].Value);
            model.Password = null;
            model.Code = null;
            return View(model);
        }

        // Same session, new auth_time; persistence as it was.
        var current = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionClaims.CookiePrincipal(user, DateTimeOffset.UtcNow),
            new AuthenticationProperties
            {
                IsPersistent = current.Properties?.IsPersistent ?? false,
                AllowRefresh = true,
            });

        TempData["Info"] = _localizer["ReauthConfirmed"].Value;
        return LocalRedirect(model.ReturnUrl!);
    }

    [CookieAuthorize]
    [HttpPost(Filters.RequireRecentAuthAttribute.ReauthPath + "/SendCode")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReauthSendCode(string? returnUrl)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction(nameof(SignIn));
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null || !user.IsActive) return RedirectToAction(nameof(SignIn));
        returnUrl = SafeReturnUrl(returnUrl);

        if (await IsRateLimitedAsync($"reauth-code:{user.Id}"))
        {
            TempData["Error"] = _localizer["TooManyVerificationAttempts"].Value;
            return RedirectToAction(nameof(Reauth), new { returnUrl });
        }

        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.EmailOtpCode = SecureTokens.Hash(otp);
        user.EmailOtpExpiresAt = DateTimeOffset.UtcNow.Add(OtpLifetime);
        // Failure count kept, as for a sign-in code.
        user.LoginChallengeHash = null;

        var variables = EmailTemplateVariables.TwoFactorCode(otp, expiryMinutes: (int)OtpLifetime.TotalMinutes);
        var emailOptions = new EmailSendOptions(EmailTemplateId.TwoFactorCode, user.Locale, user.Id);
        var queued = _email.TryEnqueue(EmailTemplateId.TwoFactorCode, user.Email, CurrentLocale, variables, emailOptions);
        await _dbContext.SaveChangesAsync();
        if (!queued)
        {
            try
            {
                await _email.SendAsync(EmailTemplateId.TwoFactorCode, user.Email, CurrentLocale, variables, emailOptions);
            }
            catch
            {
                TempData["Error"] = _localizer["TwoFactorCodeFailed"].Value;
                return RedirectToAction(nameof(Reauth), new { returnUrl });
            }
        }

        TempData["Info"] = _localizer["TwoFactorCodeSent"].Value;
        return RedirectToAction(nameof(Reauth), new { returnUrl, codeSent = true });
    }

    /// <summary>Documents and hidden version fields of the sign-up form.</summary>
    private async Task PrepareSignUpFormAsync(
        SignUpViewModel model,
        ConsentDocumentResolver consentDocuments,
        P4BackofficeProductOptions backoffice,
        IReadOnlyList<ConsentDocumentVersion>? documents = null,
        ConsentPromptsCache? prompts = null)
    {
        model.Documents = documents ?? await consentDocuments.ResolveAsync(
            ConsentRequirements.From(backoffice.Consents), CurrentLocale, HttpContext.RequestAborted);
        model.ShowVersions = backoffice.ModeFor(BackofficeModule.Consents) != BackofficeMode.Off;
        model.Prompt = PromptFor(prompts, model.Documents);
        // The form always carries the versions in force now, also when it is shown again.
        model.ShownVersions = model.Documents.ToDictionary(d => d.Document, d => d.Version, StringComparer.Ordinal);
        model.ShownHashes = model.Documents.Where(d => d.ContentHash != null)
            .ToDictionary(d => d.Document, d => d.ContentHash!, StringComparer.Ordinal);
        ModelState.Remove(nameof(SignUpViewModel.ShownVersions));
        ModelState.Remove(nameof(SignUpViewModel.ShownHashes));
    }

    /// <summary>
    /// The wording the form said it showed, as the service's `prompt` reference. Key, version
    /// and hash come from the hidden fields — by the time the form comes back a newer version
    /// may be published, and the record has to name what was read.
    ///
    /// Which documents that sentence covers is taken from this app's own copy, never from the
    /// form. The service refuses an event whose prompt is not linked to its document, and one
    /// refused event takes the whole batch with it (`RetryPolicy` marks a 4xx dead, so the
    /// other consents in that signup never arrive). A claim this app cannot attest to is
    /// therefore dropped: the consent is recorded without the reference, as it was before the
    /// service published any wording.
    /// </summary>
    private ConsentPromptRef? PromptRefFromForm(ConsentPromptsCache? prompts, string? key, int? version, string? hash)
    {
        if (prompts is null || string.IsNullOrWhiteSpace(key) || version is not > 0 || string.IsNullOrWhiteSpace(hash)) return null;
        var held = prompts.Peek(ConsentPromptContexts.Registration, CurrentLocale)?.Prompts
            .FirstOrDefault(p => p.Key == key && p.PromptVersion == version);
        if (held is null || held.PromptHash != hash || held.Documents.Count == 0) return null;
        return new ConsentPromptRef(key!, version.Value, hash!, held.Documents);
    }

    /// <summary>
    /// The sentence the service publishes for this checkbox, from what is already in memory —
    /// the form never waits for the service. Nothing cached yet means this app's own wording
    /// this time, and a background refresh so the next visitor gets the published one.
    ///
    /// It stands for the checkbox only when it names exactly the documents being granted here.
    /// A sentence that mentions more (or fewer) would describe a different decision than the
    /// one the tick records.
    /// </summary>
    private BackofficeConsentPrompt? PromptFor(ConsentPromptsCache? prompts, IReadOnlyList<ConsentDocumentVersion> documents)
    {
        if (prompts is null) return null;
        var entry = prompts.Peek(ConsentPromptContexts.Registration, CurrentLocale);
        if (entry is null || !prompts.IsFresh(entry))
        {
            RefreshPromptsInBackground(prompts);
        }

        var prompt = ConsentPromptSelection.ForSingleCheckbox(entry?.Prompts);
        if (prompt is null) return null;
        var granted = documents.Where(d => d.Action == ConsentActions.Granted).Select(d => d.Document).ToHashSet(StringComparer.Ordinal);
        return prompt.Documents.SetEquals(granted) ? prompt : null;
    }

    /// <summary>Fire and forget: a page is never held up by it.</summary>
    private void RefreshPromptsInBackground(ConsentPromptsCache prompts)
    {
        var log = HttpContext.RequestServices.GetService<ILogger<AccountController>>();
        var locale = CurrentLocale;
        _ = Task.Run(async () =>
        {
            try
            {
                await prompts.RefreshAsync(ConsentPromptContexts.Registration, locale, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The cached copy, or this app's own wording, carries the page.
            }
            catch (Exception ex)
            {
                // Nothing observes a discarded task, so anything unexpected would
                // otherwise be a defect nobody ever hears about.
                log?.LogError(ex, "Consent prompts could not be refreshed");
            }
        });
    }

    /// <summary>
    /// What to record per document: the accepted version and the server's hash of its text.
    /// The form's hash is only compared (a minor correction can change the text within a version).
    /// </summary>
    private static async Task<IReadOnlyList<ConsentChoice>> ConsentChoicesAsync(
        IReadOnlyList<ConsentDocumentVersion> documents,
        IReadOnlyDictionary<string, int> accepted,
        IReadOnlyDictionary<string, string>? shownHashes,
        ConsentDocumentResolver consentDocuments,
        ILogger logger)
    {
        var choices = new List<ConsentChoice>();
        foreach (var document in documents)
        {
            var version = accepted[document.Document];
            var hash = version == document.Version
                ? document.ContentHash
                : await consentDocuments.HashAsync(document.Document, document.Locale, version);
            if (shownHashes != null && shownHashes.TryGetValue(document.Document, out var shown)
                && hash != null && !string.Equals(shown, hash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("[consents] {Document} v{Version}: the form showed another text hash than the one recorded",
                    document.Document, version);
            }
            choices.Add(new ConsentChoice(document, version, hash));
        }
        return choices;
    }

    /// <summary>
    /// End user's IP for the consent evidence; sent to the service only for the DPA, which stores it
    /// truncated. The same helper as the rate limits (#78): behind Cloudflare the client, not the tunnel.
    /// </summary>
    private string? ClientIp() => DotNetSigningServer.Middleware.ClientIp.AddressOf(HttpContext)?.ToString();

    /// <summary>Re-consent: documents the signed-in user still has to confirm (see <c>RequireCurrentConsentFilter</c>).</summary>
    [CookieAuthorize]
    [HttpGet("/Account/Consent")]
    public async Task<IActionResult> Consent(
        string? returnUrl,
        [FromServices] IConsentStatusProvider consentStatus,
        [FromServices] ConsentDocumentResolver consentDocuments,
        [FromServices] IOptions<P4BackofficeProductOptions> backoffice,
        [FromServices] ConsentPromptsCache? prompts = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction(nameof(SignIn));
        }

        var status = await consentStatus.GetAsync(userId.Value, HttpContext.RequestAborted);
        if (status.IsCurrent)
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        var model = new ConsentViewModel { ReturnUrl = returnUrl };
        await PrepareConsentFormAsync(model, status, consentDocuments, backoffice.Value, prompts);
        return View(model);
    }

    [CookieAuthorize]
    [HttpPost("/Account/Consent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Consent(
        ConsentViewModel model,
        [FromServices] IConsentStatusProvider consentStatus,
        [FromServices] ConsentStatusCache consentCache,
        [FromServices] ConsentDocumentResolver consentDocuments,
        [FromServices] ConsentService consents,
        [FromServices] IOptions<P4BackofficeProductOptions> backoffice,
        [FromServices] TimeProvider time,
        [FromServices] ILogger<AccountController> logger,
        [FromServices] ConsentPromptsCache? prompts = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction(nameof(SignIn));
        }

        consentCache.Invalidate(userId.Value);
        var status = await consentStatus.GetAsync(userId.Value, HttpContext.RequestAborted);
        if (status.IsCurrent)
        {
            return LocalRedirect(SafeReturnUrl(model.ReturnUrl));
        }

        var shownHashes = model.ShownHashes;
        var shownVersions = model.ShownVersions;
        await PrepareConsentFormAsync(model, status, consentDocuments, backoffice.Value, prompts);
        if (model.RequiresCheckbox && !model.Accept)
        {
            ModelState.AddModelError(nameof(ConsentViewModel.Accept), _localizer["SignUpAcceptTermsRequired"].Value);
            return View(model);
        }

        var accepted = ConsentVersionCheck.Accept(shownVersions, model.Documents, time.GetUtcNow());
        if (accepted is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["LegalVersionOutdated"].Value);
            return View(model);
        }

        var choices = await ConsentChoicesAsync(model.Documents, accepted, shownHashes, consentDocuments, logger);
        consents.RecordReconsent(
            userId.Value,
            choices,
            status.HasAnyRecord ? ConsentService.Flows.Reconsent : ConsentService.Flows.Initial,
            Request.Headers.UserAgent.ToString(),
            ClientIp(),
            PromptRefFromForm(prompts, model.PromptKey, model.PromptVersion, model.PromptHash));
        await _dbContext.SaveChangesAsync();
        consentCache.Invalidate(userId.Value);

        return LocalRedirect(SafeReturnUrl(model.ReturnUrl));
    }

    private async Task PrepareConsentFormAsync(
        ConsentViewModel model,
        ConsentStatus status,
        ConsentDocumentResolver consentDocuments,
        P4BackofficeProductOptions backoffice,
        ConsentPromptsCache? prompts = null)
    {
        model.Prompt = null;
        var outstanding = status.Outstanding.Select(o => o.Document).ToHashSet(StringComparer.Ordinal);
        var requirements = ConsentRequirements.From(backoffice.Consents).Where(r => outstanding.Contains(r.Document));
        model.Documents = await consentDocuments.ResolveAsync(requirements, CurrentLocale, HttpContext.RequestAborted);
        model.Initial = !status.HasAnyRecord;
        model.ShownVersions = model.Documents.ToDictionary(d => d.Document, d => d.Version, StringComparer.Ordinal);
        model.ShownHashes = model.Documents.Where(d => d.ContentHash != null)
            .ToDictionary(d => d.Document, d => d.ContentHash!, StringComparer.Ordinal);
        // Only for what this submission actually decides: when `terms` needs
        // confirming again and `dpa` does not, a sentence naming both would ask
        // for a decision nobody is making here.
        model.Prompt = PromptFor(prompts, model.Documents);
        ModelState.Remove(nameof(ConsentViewModel.ShownVersions));
        ModelState.Remove(nameof(ConsentViewModel.ShownHashes));
    }

    /// <summary>A local return URL, else the account page (in the current language).</summary>
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(Index), "Account") ?? "/Account";

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
