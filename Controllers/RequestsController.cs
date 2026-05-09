using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

[Authorize]
public class RequestsController : Controller
{
    private const int PageSize = 50;
    private static readonly string[] AllowedFilters = { "all", "success", "error" };

    private readonly ApplicationDbContext _dbContext;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    public RequestsController(ApplicationDbContext dbContext, IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _localizer = localizer;
    }

    [HttpGet("/Requests")]
    public async Task<IActionResult> Index(string? filter = "all", int page = 1)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        if (string.IsNullOrWhiteSpace(filter) || !AllowedFilters.Contains(filter, StringComparer.OrdinalIgnoreCase))
        {
            filter = "all";
        }
        if (page < 1) page = 1;

        var baseQuery = _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.UserId == userId.Value);

        var filtered = filter.ToLowerInvariant() switch
        {
            "success" => baseQuery.Where(r => r.Status == UsageRecordStatus.Success),
            "error" => baseQuery.Where(r => r.Status == UsageRecordStatus.Error),
            _ => baseQuery,
        };

        var totalCount = await filtered.CountAsync();

        var rows = await filtered
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(r => new RequestRow
            {
                Id = r.Id,
                CreatedAt = r.CreatedAt,
                Operation = r.Operation,
                Status = r.Status,
                Credits = r.Count,
                BaseCost = r.BaseCost,
                Tier = r.Tier,
                ErrorCode = r.ErrorCode,
                ErrorMessage = r.ErrorMessage,
                HttpStatusCode = r.HttpStatusCode,
                DurationMs = r.DurationMs,
            })
            .ToListAsync();

        var since30d = DateTimeOffset.UtcNow.AddDays(-30);
        var stats = await baseQuery
            .Where(r => r.CreatedAt >= since30d)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Credits = g.Sum(r => r.Count) })
            .ToListAsync();

        var successStats = stats.FirstOrDefault(s => s.Status == UsageRecordStatus.Success);
        var errorStats = stats.FirstOrDefault(s => s.Status == UsageRecordStatus.Error);

        var creditsRemaining = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => (int?)u.CreditsRemaining)
            .FirstOrDefaultAsync();

        var vm = new RequestsViewModel
        {
            Rows = rows,
            Filter = filter.ToLowerInvariant(),
            Page = page,
            PageSize = PageSize,
            TotalCount = totalCount,
            CreditsRemaining = creditsRemaining ?? 0,
            SuccessCount30d = successStats?.Count ?? 0,
            CreditsSpent30d = successStats?.Credits ?? 0,
            ErrorCount30d = errorStats?.Count ?? 0,
        };

        return View(vm);
    }

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    public class RequestsViewModel
    {
        public List<RequestRow> Rows { get; set; } = new();
        public string Filter { get; set; } = "all";
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public int TotalCount { get; set; }
        public int CreditsRemaining { get; set; }
        public int SuccessCount30d { get; set; }
        public int CreditsSpent30d { get; set; }
        public int ErrorCount30d { get; set; }

        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        public bool HasPrevious => Page > 1;
        public bool HasNext => Page < TotalPages;
    }

    public class RequestRow
    {
        public Guid Id { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? Operation { get; set; }
        public UsageRecordStatus Status { get; set; }
        public int Credits { get; set; }
        public int BaseCost { get; set; }
        public int Tier { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? HttpStatusCode { get; set; }
        public int? DurationMs { get; set; }
    }
}
