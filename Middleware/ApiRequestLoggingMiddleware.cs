using System.Diagnostics;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Records a UsageRecord with Status=Error for any non-success /api/* response when an
/// authenticated user is known. Successful billable operations are still logged by
/// ApiControllerBase.DebitUserAsync. This middleware exists so users can see their
/// failed requests on the Requests page without losing any data — but it deliberately
/// captures NO request/response bodies, so PDF bytes, signatures, and other sensitive
/// payload never reach the database.
/// </summary>
public class ApiRequestLoggingMiddleware
{
    public const string AuthenticatedUserIdItemKey = "P4PDF.AuthenticatedUserId";
    public const string SkipLoggingItemKey = "P4PDF.SkipApiRequestLogging";

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiRequestLoggingMiddleware> _logger;

    public ApiRequestLoggingMiddleware(RequestDelegate next, ILogger<ApiRequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path;
        var isApi = path.HasValue && path.Value!.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

        if (!isApi)
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            try
            {
                await TryLogFailureAsync(context, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // Never let logging interfere with the response.
                _logger.LogWarning(ex, "ApiRequestLoggingMiddleware: failed to record request log");
            }
        }
    }

    private async Task TryLogFailureAsync(HttpContext context, long elapsedMs)
    {
        var statusCode = context.Response.StatusCode;
        if (statusCode < 400)
        {
            return; // success — DebitUserAsync handles billable success records
        }

        if (context.Items.TryGetValue(SkipLoggingItemKey, out var skip) && skip is true)
        {
            return;
        }

        if (!context.Items.TryGetValue(AuthenticatedUserIdItemKey, out var rawUserId)
            || rawUserId is not Guid userId
            || userId == Guid.Empty)
        {
            return; // No identified user → can't attribute the request.
        }

        var operation = MapPathToOperation(context.Request.Path.Value);
        var (errorCode, errorMessage) = ClassifyStatus(statusCode);

        var dbContext = context.RequestServices.GetService<ApplicationDbContext>();
        if (dbContext == null)
        {
            return;
        }

        dbContext.UsageRecords.Add(new UsageRecord
        {
            UserId = userId,
            Count = 0,
            BaseCost = 0,
            Tier = 1,
            Operation = operation,
            Status = UsageRecordStatus.Error,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            HttpStatusCode = statusCode,
            DurationMs = (int)Math.Min(elapsedMs, int.MaxValue),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync();
    }

    private static string? MapPathToOperation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // /api/<segment>[/<segment>...] → trimmed segments joined by "/", capped at 32 chars
        var trimmed = path.TrimStart('/');
        const string prefix = "api/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = trimmed[prefix.Length..];
        // Strip query/route values that look like GUIDs to keep the operation grouping stable.
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !Guid.TryParse(s, out _))
            .Take(2)
            .ToArray();

        var op = string.Join('/', segments);
        if (op.Length > 32) op = op[..32];
        return string.IsNullOrEmpty(op) ? null : op;
    }

    private static (string code, string message) ClassifyStatus(int statusCode) => statusCode switch
    {
        400 => ("BAD_REQUEST", "Invalid request"),
        401 => ("UNAUTHORIZED", "Authentication required"),
        402 => ("INSUFFICIENT_CREDITS", "Not enough credits"),
        403 => ("FORBIDDEN", "Access denied"),
        404 => ("NOT_FOUND", "Resource not found"),
        409 => ("CONFLICT", "Conflict with current state"),
        410 => ("GONE", "Resource expired"),
        413 => ("PAYLOAD_TOO_LARGE", "Request body too large"),
        429 => ("RATE_LIMITED", "Too many requests"),
        502 => ("UPSTREAM_ERROR", "Upstream service unavailable"),
        503 => ("SERVICE_UNAVAILABLE", "Service unavailable"),
        504 => ("UPSTREAM_TIMEOUT", "Upstream timed out"),
        >= 500 => ("INTERNAL_ERROR", "Internal server error"),
        _ => ("ERROR", $"HTTP {statusCode}"),
    };
}
