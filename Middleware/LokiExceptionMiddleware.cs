using DotNetSigningServer.Services;
using DotNetSigningServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace DotNetSigningServer.Middleware;

public class LokiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LokiClient _lokiClient;
    private readonly ILogger<LokiExceptionMiddleware> _logger;

    public LokiExceptionMiddleware(RequestDelegate next, LokiClient lokiClient, ILogger<LokiExceptionMiddleware> logger)
    {
        _next = next;
        _lokiClient = lokiClient;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var path = context.Request.Path.HasValue ? context.Request.Path.Value : "unknown";
            var traceId = context.TraceIdentifier;
            var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.User?.FindFirstValue("sub")
                         ?? "anonymous";

            _logger.LogError(ex, "Unhandled exception on {Path} trace {TraceId}", path, traceId);
            await _lokiClient.LogExceptionAsync(ex, path, traceId, userId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers["X-Trace-Id"] = traceId;

            var isApi = context.Request.Path.HasValue &&
                        context.Request.Path.Value!.StartsWith("/api", StringComparison.OrdinalIgnoreCase);

            if (isApi)
            {
                context.Response.ContentType = "application/json";
                var payload = new { error = true, errorMessage = "An internal error occurred.", traceId };
                await context.Response.WriteAsJsonAsync(payload);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await RenderViewAsync(context, "Shared/Error", new ErrorViewModel
            {
                TraceId = traceId,
                ErrorMessage = "We hit an unexpected error while processing your request."
            });
        }
    }

    private static async Task RenderViewAsync(HttpContext context, string viewName, ErrorViewModel model)
    {
        var serviceProvider = context.RequestServices;
        var viewEngine = serviceProvider.GetRequiredService<IRazorViewEngine>();
        var tempDataFactory = serviceProvider.GetRequiredService<ITempDataDictionaryFactory>();
        var metadataProvider = serviceProvider.GetRequiredService<IModelMetadataProvider>();

        var actionContext = new ActionContext(context, context.GetRouteData(), new ActionDescriptor());
        // Try absolute path first to avoid lookup failures in middleware
        var absoluteView = viewEngine.GetView(executingFilePath: null, viewPath: $"/Views/{viewName}.cshtml", isMainPage: true);
        var viewResult = absoluteView.Success ? absoluteView : viewEngine.FindView(actionContext, viewName, isMainPage: true);

        if (!viewResult.Success)
        {
            await context.Response.WriteAsync($"An internal error occurred. TraceId: {model.TraceId}");
            return;
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary(metadataProvider, new ModelStateDictionary())
        {
            Model = model
        };

        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            tempDataFactory.GetTempData(context),
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        await context.Response.WriteAsync(writer.ToString());
    }
}
