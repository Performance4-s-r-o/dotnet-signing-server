using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Filters;

/// <summary>
/// Holds a slot of pool H or A (#69) for the whole action. A resource filter, so the slot is
/// taken before the body is bound: a request that has to wait does not hold its document in
/// memory meanwhile. Off pool = no-op. Full after the short wait = 503 + Retry-After, the same
/// answer as the global cap.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CapacityPoolAttribute : Attribute, IAsyncResourceFilter
{
    public const int RetryAfterSeconds = 10;

    public CapacityPoolAttribute(CapacityPoolKind kind) => Kind = kind;

    public CapacityPoolKind Kind { get; }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var pools = context.HttpContext.RequestServices.GetService<CapacityPools>();
        var pool = pools?.Get(Kind);
        if (pool == null || !pool.Enabled)
        {
            await next();
            return;
        }

        bool entered;
        try
        {
            entered = await pool.TryEnterAsync(pools!.Wait, context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away while waiting; nobody is left to answer.
            context.Result = new EmptyResult();
            return;
        }

        if (!entered)
        {
            var localizer = context.HttpContext.RequestServices
                .GetRequiredService<IStringLocalizerFactory>().Create(typeof(SharedStrings));
            context.HttpContext.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Result = new ObjectResult(new
            {
                code = "SERVER_AT_CAPACITY",
                message = localizer["ServerAtCapacity"].Value,
                retryAfter = RetryAfterSeconds,
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        try
        {
            await next();
        }
        finally
        {
            pool.Release();
        }
    }
}
