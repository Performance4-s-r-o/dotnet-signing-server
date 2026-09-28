using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Support;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

/// <summary>
/// In-app contact form for signed-in users.
///
/// <c>P4Backoffice:Modules:Support=On</c>: the ticket is queued in the backoffice outbox in the
/// same transaction and attempted right away for at most <see cref="DispatchNowLimit"/>; the
/// user sees the ticket number, or "received" when the service did not answer in time — never
/// an error because the service is down (the dispatcher delivers it later, exactly once).
/// Otherwise (Off, Shadow) the ticket goes to osTicket directly, as before; that path needs the
/// OsTicket configuration, and with neither the routes 404 and the nav entry is hidden.
/// </summary>
[Authorize]
public class SupportController : Controller
{
    /// <summary>How long a submission waits for the service's answer before showing "received".</summary>
    public static readonly TimeSpan DispatchNowLimit = TimeSpan.FromSeconds(5);

    /// <summary>A repeated submission of the same form is recognised for this long.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(1);

    private readonly ApplicationDbContext _dbContext;
    private readonly IStringLocalizer<SharedStrings> _localizer;
    private readonly OsTicketOptions _osTicket;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SupportController> _logger;
    private readonly P4BackofficeProductOptions _backoffice;
    private readonly SupportCategoriesProvider _categories;
    private readonly IBackofficeOutbox? _outbox;
    private readonly OutboxProcessor? _processor;
    private readonly TimeProvider _time;

    private static readonly Dictionary<string, int> PriorityMap = new()
    {
        ["low"] = 3,
        ["normal"] = 2,
        ["high"] = 1,
    };

    public SupportController(
        ApplicationDbContext dbContext,
        IStringLocalizer<SharedStrings> localizer,
        IOptions<OsTicketOptions> osTicket,
        IHttpClientFactory httpClientFactory,
        ILogger<SupportController> logger,
        IOptions<P4BackofficeProductOptions> backoffice,
        SupportCategoriesProvider categories,
        IBackofficeOutbox? outbox = null,
        OutboxProcessor? processor = null,
        TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _localizer = localizer;
        _osTicket = osTicket.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _backoffice = backoffice.Value;
        _categories = categories;
        _outbox = outbox;
        _processor = processor;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Tickets go through the service (outbox) rather than to osTicket directly.</summary>
    private bool ServiceOn =>
        _backoffice.ModeFor(BackofficeModule.Support) == BackofficeMode.On && _outbox != null;

    private bool Enabled => ServiceOn || _osTicket.IsConfigured;

    [HttpGet("/support")]
    public async Task<IActionResult> Index()
    {
        if (!Enabled)
        {
            return NotFound();
        }

        ViewData["UserEmail"] = (await GetCurrentUserAsync())?.Email ?? GetUserEmailClaim();
        ViewData["SupportCategories"] = _categories.For(CultureInfo.CurrentUICulture.Name);
        ViewData["SupportFormKey"] = Guid.NewGuid().ToString("N");
        return View();
    }

    [HttpPost("/support/ticket")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitTicket(
        string subject,
        string message,
        string category,
        string priority,
        string? formKey = null)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
        {
            TempData["Error"] = _localizer["FieldsRequired"].Value;
            return RedirectToAction(nameof(Index));
        }

        var user = await GetCurrentUserAsync();
        if (ServiceOn)
        {
            return await SubmitToServiceAsync(user, subject, message, category, priority, formKey);
        }

        var userEmail = user?.Email ?? GetUserEmailClaim();

        var plan = user?.IsEnterprise == true ? "Enterprise" : "Standard";
        var credits = user?.CreditsRemaining.ToString() ?? "N/A";

        // HTML-encode before building the fragment: the message is user-controlled
        // and osTicket decodes the data: URI and renders it in the agent console.
        var body =
            $"<p>{WebUtility.HtmlEncode(message.Trim()).Replace("\n", "<br>")}</p>"
            + "<hr>"
            + $"<p><strong>User:</strong> {WebUtility.HtmlEncode(userEmail)}<br>"
            + $"<strong>Plan:</strong> {plan}<br>"
            + $"<strong>Credits:</strong> {WebUtility.HtmlEncode(credits)}</p>";

        var payload = new
        {
            name = SupportTicketRequest.ReporterName(userEmail),
            email = userEmail,
            subject = subject.Trim(),
            message = $"data:text/html,{Uri.EscapeDataString(body)}",
            topicId = _osTicket.ResolveTopicId(category),
            priority = PriorityMap.TryGetValue(priority ?? "", out var mapped) ? mapped : PriorityMap["normal"],
            source = "API",
        };

        try
        {
            var http = _httpClientFactory.CreateClient("osticket");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_osTicket.Url!.TrimEnd('/')}/api/tickets.json")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("X-API-Key", _osTicket.ApiKey);

            using var response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "osTicket rejected ticket for {Email}: {Status} {Body}",
                    userEmail, (int)response.StatusCode, errorBody);
                TempData["Error"] = _localizer["SupportSubmitFailed"].Value;
                return RedirectToAction(nameof(Index));
            }

            var ticketId = (await response.Content.ReadAsStringAsync()).Trim();
            _logger.LogInformation("osTicket ticket {TicketId} created for {Email}", ticketId, userEmail);

            TempData["Info"] = string.IsNullOrWhiteSpace(ticketId)
                ? _localizer["SupportTicketCreated"].Value
                : _localizer["SupportTicketCreatedWithId", ticketId].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach osTicket for {Email}", userEmail);
            TempData["Error"] = _localizer["SupportUnavailable"].Value;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>
    /// Support=On: queue the ticket (one <c>SaveChanges</c>), then give the service
    /// <see cref="DispatchNowLimit"/> to answer. A repeated submission of the same form (same
    /// <paramref name="formKey"/>) is not queued again.
    /// </summary>
    private async Task<IActionResult> SubmitToServiceAsync(
        User? user, string subject, string message, string category, string priority, string? formKey)
    {
        if (user == null)
        {
            // The account no longer exists; nothing sensible to file the ticket under.
            TempData["Error"] = _localizer["SupportSubmitFailed"].Value;
            return RedirectToAction(nameof(Index));
        }

        var subjectRef = OutboxSubjectRef(user.Id, formKey);
        var itemId = subjectRef == null ? (Guid?)null : await FindQueuedAsync(subjectRef);
        if (itemId == null)
        {
            var locale = SupportCategoriesWorker.LocaleFor(CultureInfo.CurrentUICulture.Name);
            var offered = _categories.For(CultureInfo.CurrentUICulture.Name).Select(c => c.Key).ToList();
            var payload = SupportTicketRequest.Build(user, new SupportTicketInput(
                Category: category,
                Subject: subject,
                Message: message,
                Priority: priority,
                Locale: locale,
                Url: SupportPageUrl(locale),
                UserAgent: Request.Headers.UserAgent.ToString(),
                AppVersion: SupportTicketRequest.ProductVersion(typeof(SupportController).Assembly)), offered);

            itemId = _outbox!.Enqueue(SupportTicketRequest.OutboxKind, payload, critical: false,
                subjectRef: subjectRef ?? ConsentRequirements.SubjectRef(user.Id));
            await _dbContext.SaveChangesAsync();
        }

        string? ticketNumber = null;
        if (_processor != null)
        {
            try
            {
                var result = await _processor.TryDispatchNowAsync(itemId.Value, DispatchNowLimit, HttpContext.RequestAborted);
                if (result.IsSent) ticketNumber = SupportTicketReceipt.TicketNumber(result.RemoteId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Queued already: the dispatcher sends it. The user is not shown an error.
                _logger.LogWarning(ex, "Support ticket {ItemId}: immediate attempt failed; left to the outbox", itemId);
            }
        }

        TempData["Info"] = ticketNumber != null
            ? _localizer["SupportTicketCreatedWithId", ticketNumber].Value
            : _localizer["SupportTicketQueued"].Value;
        return RedirectToAction(nameof(Index));
    }

    /// <summary><c>dotnet:user:{id}/support/{formKey}</c>; null when the form sent no valid key.</summary>
    internal static string? OutboxSubjectRef(Guid userId, string? formKey) =>
        Guid.TryParse(formKey, out var key)
            ? $"{ConsentRequirements.SubjectRef(userId)}/support/{key:N}"
            : null;

    private async Task<Guid?> FindQueuedAsync(string subjectRef)
    {
        var since = _time.GetUtcNow() - DuplicateWindow;
        var id = await _dbContext.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.CreatedAt >= since
                        && i.Kind == SupportTicketRequest.OutboxKind
                        && i.SubjectRef == subjectRef)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync();
        if (id != null)
        {
            _logger.LogInformation("Support ticket {ItemId} was submitted again; not queued twice", id);
        }
        return id;
    }

    /// <summary>Where the user was: the support page itself (the form lives only there).</summary>
    private string? SupportPageUrl(string locale) =>
        Request.Host.HasValue
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}{CultureUrls.Prefix(locale)}/support"
            : null;

    /// <summary>The signed-in user, looked up by id (<c>NameIdentifier</c>); null when the row is gone.</summary>
    private async Task<User?> GetCurrentUserAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return null;
        }

        return await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
    }

    /// <summary>
    /// The e-mail address from the cookie. Sign-in stores it as <c>ClaimTypes.Name</c>; the
    /// e-mail claim types are only read in case another scheme sets them.
    /// </summary>
    private string GetUserEmailClaim() =>
        User.FindFirst(ClaimTypes.Email)?.Value
        ?? User.FindFirst("email")?.Value
        ?? User.FindFirst(ClaimTypes.Name)?.Value
        ?? string.Empty;
}
