using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

/// <summary>
/// In-app contact form for signed-in users, backed by an osTicket helpdesk.
/// The whole
/// feature is gated on the OsTicket configuration: with no Url/ApiKey the
/// routes 404 and the nav entry is hidden.
/// </summary>
[Authorize]
public class SupportController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IStringLocalizer<SharedStrings> _localizer;
    private readonly OsTicketOptions _osTicket;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SupportController> _logger;

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
        ILogger<SupportController> logger)
    {
        _dbContext = dbContext;
        _localizer = localizer;
        _osTicket = osTicket.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("/support")]
    public async Task<IActionResult> Index()
    {
        if (!_osTicket.IsConfigured)
        {
            return NotFound();
        }

        ViewData["UserEmail"] = (await GetCurrentUserAsync())?.Email ?? GetUserEmailClaim();
        return View();
    }

    [HttpPost("/support/ticket")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitTicket(
        string subject,
        string message,
        string category,
        string priority)
    {
        if (!_osTicket.IsConfigured)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
        {
            TempData["Error"] = _localizer["FieldsRequired"].Value;
            return RedirectToAction(nameof(Index));
        }

        var user = await GetCurrentUserAsync();
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
            name = ReporterName(userEmail),
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

    /// <summary>
    /// Name shown to support agents. Accounts have no display name, so it is the e-mail
    /// address (as the helpdesk shows it next to the ticket anyway).
    /// </summary>
    internal static string ReporterName(string email) => email;
}
