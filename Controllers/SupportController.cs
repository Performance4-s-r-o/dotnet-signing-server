using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using DotNetSigningServer.Data;
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
    public IActionResult Index()
    {
        if (!_osTicket.IsConfigured)
        {
            return NotFound();
        }

        ViewData["UserEmail"] = GetUserEmail();
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

        var userEmail = GetUserEmail();
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == userEmail);

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
            name = User.Identity?.Name ?? userEmail.Split('@')[0],
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

    private string GetUserEmail() =>
        User.FindFirst(ClaimTypes.Email)?.Value
        ?? User.FindFirst("email")?.Value
        ?? string.Empty;
}
