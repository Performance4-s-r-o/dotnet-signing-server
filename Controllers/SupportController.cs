using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using DotNetSigningServer.Data;
using DotNetSigningServer.Resources;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

[Authorize]
public class SupportController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IStringLocalizer<SharedStrings> _localizer;
    private readonly IConfiguration _configuration;

    public SupportController(
        ApplicationDbContext dbContext,
        IStringLocalizer<SharedStrings> localizer,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _localizer = localizer;
        _configuration = configuration;
    }

    [HttpGet("/support")]
    public IActionResult Index()
    {
        var userEmail = User.FindFirst(ClaimTypes.Email)?.Value
                        ?? User.FindFirst("email")?.Value ?? "";
        ViewData["UserEmail"] = userEmail;
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
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
        {
            TempData["Error"] = _localizer["FieldsRequired"].Value;
            return RedirectToAction(nameof(Index));
        }

        var userEmail = User.FindFirst(ClaimTypes.Email)?.Value
                        ?? User.FindFirst("email")?.Value ?? "unknown";
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value;

        var osTicketUrl = _configuration["OsTicket:Url"];
        var osTicketApiKey = _configuration["OsTicket:ApiKey"];

        if (!string.IsNullOrWhiteSpace(osTicketUrl) && !string.IsNullOrWhiteSpace(osTicketApiKey))
        {
            // Submit to osTicket
            var payload = new
            {
                name = User.Identity?.Name ?? userEmail.Split("@")[0],
                email = userEmail,
                subject,
                message = $"data:text/html,{Uri.EscapeDataString($"<p>{message.Replace("\n", "<br>")}</p><hr><p><strong>User:</strong> {userEmail}</p>")}",
                topicId = _configuration[$"OsTicket:Topic:{category}"] ?? "1",
                priority = priority switch
                {
                    "high" => 1,
                    "low" => 3,
                    _ => 2,
                },
                source = "API",
            };

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-API-Key", osTicketApiKey);
            var response = await http.PostAsJsonAsync($"{osTicketUrl}/api/tickets.json", payload);

            if (response.IsSuccessStatusCode)
            {
                TempData["Info"] = _localizer["SettingsSaved"].Value;
                return RedirectToAction(nameof(Index));
            }
        }

        // Fallback: log to console (no local DB table in dotnet app)
        Console.WriteLine($"[Support Ticket] From: {userEmail}, Subject: {subject}, Category: {category}, Priority: {priority}");
        TempData["Info"] = _localizer["SettingsSaved"].Value;
        return RedirectToAction(nameof(Index));
    }
}
