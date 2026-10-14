using System.Security.Claims;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Middleware;

/// <summary>
/// #69: the order in which the user's and the global slot are taken, and that every slot taken
/// is given back on every path. No database is registered, so the middleware falls back to the
/// limits in <see cref="BillingOptions"/> for every user.
/// </summary>
public class UserConcurrencyMiddlewareTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    private static IServiceProvider Services(BillingOptions billing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddSingleton<IOptions<BillingOptions>>(Microsoft.Extensions.Options.Options.Create(billing));
        return services.BuildServiceProvider();
    }

    private static UserConcurrencyMiddleware Create(BillingOptions billing, RequestDelegate next) =>
        new(next, NullLogger<UserConcurrencyMiddleware>.Instance, Microsoft.Extensions.Options.Options.Create(billing));

    private static HttpContext Request(IServiceProvider services, Guid userId, CancellationToken aborted = default)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
        };
        context.Request.Path = "/api/sign";
        context.Response.Body = new MemoryStream();
        context.RequestAborted = aborted;
        return context;
    }

    private static BillingOptions Billing(int userLimit, int queueSeconds, int globalLimit) => new()
    {
        ConcurrencyDefaultLimit = userLimit,
        ConcurrencyQueueTimeoutSeconds = queueSeconds,
        GlobalConcurrencyLimit = globalLimit,
    };

    [Fact]
    public async Task Ordinary_request_passes_and_gives_its_slots_back()
    {
        var billing = Billing(userLimit: 3, queueSeconds: 0, globalLimit: 2);
        var services = Services(billing);
        var middleware = Create(billing, ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });

        var context = Request(services, Guid.NewGuid());
        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, context.Items[UserConcurrencyMiddleware.TierItemKey]);
        Assert.Equal(2, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Thousand_rejections_from_a_full_user_queue_do_not_drain_the_global_cap()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 2);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx =>
        {
            if (ctx.Request.Headers.ContainsKey("X-Hold")) await release.Task;
            ctx.Response.StatusCode = 200;
        });

        var busyUser = Guid.NewGuid();
        var holder = Request(services, busyUser);
        holder.Request.Headers["X-Hold"] = "1";
        var held = middleware.InvokeAsync(holder);

        for (var i = 0; i < 1000; i++)
        {
            var rejected = Request(services, busyUser);
            await middleware.InvokeAsync(rejected);
            Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        }

        // One global slot is held by the busy user's running request; the other must still be free.
        var other = Request(services, Guid.NewGuid());
        await middleware.InvokeAsync(other);
        Assert.Equal(200, other.Response.StatusCode);

        release.SetResult();
        await held.WaitAsync(Bounded);
        Assert.Equal(2, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Requests_queued_behind_one_user_do_not_hold_global_slots()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 30, globalLimit: 2);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx =>
        {
            if (ctx.Request.Headers.ContainsKey("X-Hold")) await release.Task;
            ctx.Response.StatusCode = 200;
        });

        var attacker = Guid.NewGuid();
        var attackerRequests = Enumerable.Range(0, 5).Select(_ =>
        {
            var ctx = Request(services, attacker);
            ctx.Request.Headers["X-Hold"] = "1";
            return (ctx, task: middleware.InvokeAsync(ctx));
        }).ToList();

        // One attacker request runs, four wait in the attacker's own queue.
        var legitimate = Request(services, Guid.NewGuid());
        await middleware.InvokeAsync(legitimate).WaitAsync(Bounded);
        Assert.Equal(200, legitimate.Response.StatusCode);

        release.SetResult();
        await Task.WhenAll(attackerRequests.Select(r => r.task)).WaitAsync(Bounded);
        Assert.All(attackerRequests, r => Assert.Equal(200, r.ctx.Response.StatusCode));
        Assert.Equal(2, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Exception_in_the_pipeline_gives_both_slots_back()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 1);
        var services = Services(billing);
        var middleware = Create(billing, _ => throw new InvalidOperationException("boom"));
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request(services, user)));

        Assert.Equal(1, middleware.GlobalSlotsAvailable);
        // The user's slot is free again too: the next request is not rejected with 429.
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request(services, user)));
    }

    [Fact]
    public async Task Request_cancelled_while_queued_leaves_without_taking_or_leaking_slots()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 30, globalLimit: 2);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx =>
        {
            if (ctx.Request.Headers.ContainsKey("X-Hold")) await release.Task;
            ctx.Response.StatusCode = 200;
        });
        var user = Guid.NewGuid();

        var holder = Request(services, user);
        holder.Request.Headers["X-Hold"] = "1";
        var held = middleware.InvokeAsync(holder);

        using var cts = new CancellationTokenSource();
        var queued = middleware.InvokeAsync(Request(services, user, cts.Token));
        cts.Cancel();
        // Without honouring the abort the request would sit in the queue for the full 30 s.
        await queued.WaitAsync(Bounded);

        release.SetResult();
        await held.WaitAsync(Bounded);
        Assert.Equal(2, middleware.GlobalSlotsAvailable);

        var next = Request(services, user);
        await middleware.InvokeAsync(next);
        Assert.Equal(200, next.Response.StatusCode);
    }

    [Fact]
    public async Task Request_cancelled_while_running_gives_both_slots_back()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 1);
        var services = Services(billing);
        var middleware = Create(billing, async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted));
        using var cts = new CancellationTokenSource();

        var running = middleware.InvokeAsync(Request(services, Guid.NewGuid(), cts.Token));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bounded));

        Assert.Equal(1, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Global_cap_full_returns_503_and_gives_the_user_slot_back()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 1);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx =>
        {
            if (ctx.Request.Headers.ContainsKey("X-Hold")) await release.Task;
            ctx.Response.StatusCode = 200;
        });

        var holder = Request(services, Guid.NewGuid());
        holder.Request.Headers["X-Hold"] = "1";
        var held = middleware.InvokeAsync(holder);

        var user = Guid.NewGuid();
        var refused = Request(services, user);
        await middleware.InvokeAsync(refused);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refused.Response.StatusCode);

        release.SetResult();
        await held.WaitAsync(Bounded);

        // Had the 503 kept the user's slot, this would be a 429.
        var retry = Request(services, user);
        await middleware.InvokeAsync(retry);
        Assert.Equal(200, retry.Response.StatusCode);
        Assert.Equal(1, middleware.GlobalSlotsAvailable);
    }
}
