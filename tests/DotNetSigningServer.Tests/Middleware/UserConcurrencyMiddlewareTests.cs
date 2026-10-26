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

    private static BillingOptions Billing(int userLimit, int queueSeconds, int globalLimit, int globalWaitMs = 0) => new()
    {
        ConcurrencyDefaultLimit = userLimit,
        ConcurrencyQueueTimeoutSeconds = queueSeconds,
        GlobalConcurrencyLimit = globalLimit,
        GlobalConcurrencyWaitMilliseconds = globalWaitMs,
    };

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bounded;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

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

        var user = Guid.NewGuid();
        var running = middleware.InvokeAsync(Request(services, user, cts.Token));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bounded));

        Assert.Equal(1, middleware.GlobalSlotsAvailable);

        // The user's own slot is back as well: the next request (limit 1, queue 0) is not refused.
        var next = Request(services, user);
        var again = Create(billing, ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
        await again.InvokeAsync(next);
        Assert.Equal(200, next.Response.StatusCode);
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

    [Fact]
    public async Task Raised_limit_keeps_running_requests_counted_and_wakes_the_queue()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 30, globalLimit: 0);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx => { await release.Task; ctx.Response.StatusCode = 200; });
        var user = Guid.NewGuid();

        var first = middleware.InvokeAsync(Request(services, user));
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 1));
        var queued = Request(services, user);
        var second = middleware.InvokeAsync(queued);

        billing.ConcurrencyDefaultLimit = 2;
        var third = Request(services, user);
        var thirdTask = middleware.InvokeAsync(third);

        // The queued request got the new slot; the old one is still counted, so the tier is 2.
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 2));
        release.SetResult();
        await Task.WhenAll(first, second, thirdTask).WaitAsync(Bounded);
        Assert.Equal(0, UserConcurrencyMiddleware.GetInFlightCount(user));
    }

    [Fact]
    public async Task Lowered_limit_counts_old_requests_and_admits_nobody_above_it()
    {
        var billing = Billing(userLimit: 3, queueSeconds: 0, globalLimit: 0);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx => { await release.Task; ctx.Response.StatusCode = 200; });
        var user = Guid.NewGuid();

        var running = Enumerable.Range(0, 3).Select(_ => middleware.InvokeAsync(Request(services, user))).ToList();
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 3));

        billing.ConcurrencyDefaultLimit = 1;
        UserConcurrencyMiddleware.InvalidateLimitCache(user);

        // Three still run, so the new limit of 1 refuses even though a replaced semaphore would have admitted one.
        var refused = Request(services, user);
        await middleware.InvokeAsync(refused);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);

        release.SetResult();
        await Task.WhenAll(running).WaitAsync(Bounded);
        Assert.Equal(0, UserConcurrencyMiddleware.GetInFlightCount(user));

        // After all finished only one at a time is admitted.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var again = Create(billing, async ctx => { await hold.Task; ctx.Response.StatusCode = 200; });
        var one = again.InvokeAsync(Request(services, user));
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 1));
        var two = Request(services, user);
        await again.InvokeAsync(two);
        Assert.Equal(StatusCodes.Status429TooManyRequests, two.Response.StatusCode);
        hold.SetResult();
        await one.WaitAsync(Bounded);
    }

    [Fact]
    public async Task Queue_timeout_gives_back_nothing_it_did_not_take()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 1, globalLimit: 0);
        var services = Services(billing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = Create(billing, async ctx => { await release.Task; ctx.Response.StatusCode = 200; });
        var user = Guid.NewGuid();

        var held = middleware.InvokeAsync(Request(services, user));
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 1));
        var timedOut = Request(services, user);
        await middleware.InvokeAsync(timedOut).WaitAsync(Bounded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, timedOut.Response.StatusCode);
        Assert.Equal(1, UserConcurrencyMiddleware.GetInFlightCount(user));

        release.SetResult();
        await held.WaitAsync(Bounded);
        Assert.Equal(0, UserConcurrencyMiddleware.GetInFlightCount(user));
    }

    [Fact]
    public async Task Global_slot_is_waited_for_briefly_and_taken_when_it_frees_up()
    {
        var billing = Billing(userLimit: 5, queueSeconds: 0, globalLimit: 1, globalWaitMs: 5000);
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
        Assert.True(await Eventually(() => middleware.GlobalSlotsAvailable == 0));

        var waiting = Request(services, Guid.NewGuid());
        var waitingTask = middleware.InvokeAsync(waiting);
        await Task.Delay(100);
        Assert.False(waitingTask.IsCompleted);

        release.SetResult();
        await Task.WhenAll(held, waitingTask).WaitAsync(Bounded);
        Assert.Equal(200, waiting.Response.StatusCode);
        Assert.Equal(1, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Global_slot_wait_expires_with_503_and_releases_the_user_slot()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 1, globalWaitMs: 150);
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
        Assert.True(await Eventually(() => middleware.GlobalSlotsAvailable == 0));

        var user = Guid.NewGuid();
        var refused = Request(services, user);
        await middleware.InvokeAsync(refused).WaitAsync(Bounded);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refused.Response.StatusCode);
        Assert.Equal(0, UserConcurrencyMiddleware.GetInFlightCount(user));

        release.SetResult();
        await held.WaitAsync(Bounded);
        Assert.Equal(1, middleware.GlobalSlotsAvailable);
    }

    [Fact]
    public async Task Request_cancelled_while_waiting_for_the_global_slot_gives_the_user_slot_back()
    {
        var billing = Billing(userLimit: 1, queueSeconds: 0, globalLimit: 1, globalWaitMs: 30000);
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
        Assert.True(await Eventually(() => middleware.GlobalSlotsAvailable == 0));

        var user = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var waiting = middleware.InvokeAsync(Request(services, user, cts.Token));
        Assert.True(await Eventually(() => UserConcurrencyMiddleware.GetInFlightCount(user) == 1));
        cts.Cancel();
        await waiting.WaitAsync(Bounded);

        Assert.Equal(0, UserConcurrencyMiddleware.GetInFlightCount(user));
        release.SetResult();
        await held.WaitAsync(Bounded);
        Assert.Equal(1, middleware.GlobalSlotsAvailable);
    }

    [Theory]
    [InlineData(null, 4, 16)]
    [InlineData(null, 24, 16)]
    [InlineData(null, 96, 64)]
    [InlineData(0, 24, 0)]
    [InlineData(7, 24, 7)]
    public void Effective_global_limit_defaults_from_cpu_count_and_zero_means_off(int? configured, int cpus, int expected)
    {
        var options = new BillingOptions { GlobalConcurrencyLimit = configured };
        Assert.Equal(expected, BillingOptions.EffectiveGlobalLimit(options, cpus));
    }
}
