using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// The app against PostgreSQL with the signing work mocked, so each test decides whether the
/// work succeeds, fails like a TSA timeout, or blocks until released.
/// </summary>
public sealed class OperationStateFixture : IAsyncLifetime
{
    public static readonly string Pdf = TestHelpers.CreateMinimalPdfBase64();

    public OutboxPostgresFixture Postgres { get; } = new();
    internal WebApplicationFactory<Program> App { get; private set; } = null!;

    /// <summary>What the next /api/sign does (the presign's HandleSign).</summary>
    public Func<string> Sign { get; set; } = () => Pdf;
    public int SignCalls;

    /// <summary>What the next /api/timestamp does.</summary>
    public Func<string> Timestamp { get; set; } = () => Pdf;
    public int TimestampCalls;

    private sealed class NoEmail : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }

    private sealed class Factory(OperationStateFixture fx) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ClientOptions.BaseAddress = new Uri("https://localhost");
            builder.UseEnvironment("Testing");
            builder.UseSetting("UseLocalDb", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", fx.Postgres.ConnectionString);
            builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();

                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(fx.Postgres.Configure);

                var signing = new Mock<PdfSigningService>(null, null, null);
                signing.Setup(s => s.HandleSign(
                        It.IsAny<SignInput>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TsaEndpoint?>()))
                    .Returns(() =>
                    {
                        Interlocked.Increment(ref fx.SignCalls);
                        return fx.Sign();
                    });
                signing.Setup(s => s.ApplyDocumentTimestamp(It.IsAny<DocumentTimestampInput>()))
                    .Returns(() =>
                    {
                        Interlocked.Increment(ref fx.TimestampCalls);
                        return fx.Timestamp();
                    });
                services.RemoveAll<PdfSigningService>();
                services.AddScoped(_ => signing.Object);
            });
        }
    }

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
        App = new Factory(this);
        _ = App.Server;
    }

    public void Reset()
    {
        Sign = () => Pdf;
        Timestamp = () => Pdf;
        SignCalls = 0;
        TimestampCalls = 0;
    }

    /// <summary>A fresh account with its own API token, so tests do not share a balance.</summary>
    public async Task<(Guid UserId, HttpClient Client)> NewAccountAsync(int credits)
    {
        await using var db = Postgres.CreateContext();
        var user = new User
        {
            Email = $"u{Guid.NewGuid():N}@example.com",
            EmailVerified = true,
            IsActive = true,
            CreditsRemaining = credits,
            // Room for the parallel requests below; the default (3, no queue) answers 429.
            MaxConcurrentOperations = 10,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        using var scope = App.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var (plaintext, hash, _) = tokens.IssueToken(user, "integration", null);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = "integration",
            TokenHash = hash,
            TokenPrefix = plaintext[..8],
            IsBrowserToken = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        return (user.Id, client);
    }

    public async Task<string> NewPresignAsync(Guid userId)
    {
        await using var db = Postgres.CreateContext();
        var row = new SigningData
        {
            UserId = userId,
            PresignedPdfPath = Path.Combine(Path.GetTempPath(), $"presign-{Guid.NewGuid():N}.pdf"),
            HashToSign = "00",
            CertificatePem = "-----BEGIN CERTIFICATE-----",
            TsaUrl = "https://tsa.example.com",
        };
        db.SigningData.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<bool> PresignExistsAsync(string id)
    {
        await using var db = Postgres.CreateContext();
        return await db.SigningData.AnyAsync(s => s.Id == id);
    }

    public async Task<int> BalanceAsync(Guid userId)
    {
        await using var db = Postgres.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.CreditsRemaining).SingleAsync();
    }

    public async Task<List<UsageRecord>> UsageAsync(Guid userId)
    {
        await using var db = Postgres.CreateContext();
        return await db.UsageRecords.Where(u => u.UserId == userId && u.Status == UsageRecordStatus.Success).ToListAsync();
    }

    public ApiOperationService Service(ApplicationDbContext db) =>
        new(db, new EphemeralDataProtectionProvider(), NullLogger<ApiOperationService>.Instance);

    public async Task DisposeAsync()
    {
        if (App != null) await App.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

/// <summary>
/// #77 (operation state, idempotent retries) and #82 (reserve before the work). Each test here
/// failed against the code before: /api/sign deleted the presign before signing, and the
/// credits were taken only after the work.
/// </summary>
[Trait("Category", "Db")]
public class OperationStateTests : IClassFixture<OperationStateFixture>, IDisposable
{
    private readonly OperationStateFixture _fx;

    public OperationStateTests(OperationStateFixture fx)
    {
        _fx = fx;
        _fx.Reset();
    }

    public void Dispose() => _fx.Reset();

    private static readonly string Pdf = OperationStateFixture.Pdf;
    private static readonly string SignedPdf = TestHelpers.CreateMinimalPdfBase64();

    private static HttpRequestMessage Post(string path, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType()) };
        if (key != null) request.Headers.Add(ApiControllerBase.IdempotencyKeyHeader, key);
        return request;
    }

    private static async Task<string?> ResultAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client, string key)
    {
        var response = await client.GetAsync($"/api/operations/{key}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    [DockerFact]
    public async Task Tsa_failure_on_sign_keeps_the_presign_and_the_retry_signs_and_charges_once()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var presign = await _fx.NewPresignAsync(userId);
        var key = Guid.NewGuid().ToString();
        var body = new SignInput { Id = presign, SignedHash = "00" };

        var calls = 0;
        _fx.Sign = () => ++calls == 1
            ? throw new TsaCommunicationException("https://tsa.example.com", "The operation has timed out.")
            : SignedPdf;

        var first = await client.SendAsync(Post("/api/sign", body, key));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(ApiOperationState.FailedUncertain, Header(first, ApiControllerBase.OperationStateHeader));
        Assert.True(await _fx.PresignExistsAsync(presign), "the presign must survive a failed attempt");
        Assert.Equal(5, await _fx.BalanceAsync(userId));

        var state = await StatusAsync(client, key);
        Assert.Equal("failed_uncertain", state.GetProperty("state").GetString());
        Assert.Equal(0, state.GetProperty("creditsCharged").GetInt32());
        Assert.Equal("TSA_UNREACHABLE", state.GetProperty("errorCode").GetString());

        var retry = await client.SendAsync(Post("/api/sign", body, key));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(SignedPdf, await ResultAsync(retry));
        Assert.Equal(4, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));
        Assert.Equal(2, _fx.SignCalls);
        Assert.False(await _fx.PresignExistsAsync(presign), "a completed signature consumes the presign");

        state = await StatusAsync(client, key);
        Assert.Equal("completed", state.GetProperty("state").GetString());
        Assert.Equal(1, state.GetProperty("creditsCharged").GetInt32());
        Assert.Equal(2, state.GetProperty("attempts").GetInt32());
    }

    [DockerFact]
    public async Task Portal_timeout_during_tsa_then_retry_with_the_same_key_gets_the_result_without_a_second_signature_or_debit()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var presign = await _fx.NewPresignAsync(userId);
        var key = Guid.NewGuid().ToString();
        var body = new SignInput { Id = presign, SignedHash = "00" };

        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        _fx.Sign = () =>
        {
            entered.Release();
            // The TSA answers late — after the portal has given up.
            release.Wait(TimeSpan.FromSeconds(30));
            return SignedPdf;
        };

        using var portalTimeout = new CancellationTokenSource();
        var first = client.SendAsync(Post("/api/sign", body, key), portalTimeout.Token);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)), "the signature never started");
        // The portal gives up. (TestServer completes the cancelled call only once the server
        // is done, so it is awaited at the end; what matters is that nobody reads it.)
        portalTimeout.Cancel();

        // Still at the TSA: the retry is told to come back, nothing is signed twice.
        Assert.Equal("running", (await StatusAsync(client, key)).GetProperty("state").GetString());
        var early = await client.SendAsync(Post("/api/sign", body, key));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("OPERATION_IN_PROGRESS", await early.Content.ReadAsStringAsync());
        Assert.NotNull(early.Headers.RetryAfter);

        release.Set();
        try { await first; } catch (OperationCanceledException) { /* the portal's own timeout */ }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await StatusAsync(client, key)).GetProperty("state").GetString() != "completed")
        {
            Assert.True(DateTime.UtcNow < deadline, "the abandoned signature never completed");
            await Task.Delay(50);
        }

        var retry = await client.SendAsync(Post("/api/sign", body, key));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", Header(retry, ApiControllerBase.ReplayedHeader));
        Assert.Equal(SignedPdf, await ResultAsync(retry));
        Assert.Equal(1, _fx.SignCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));
    }

    [DockerFact]
    public async Task Repeated_sign_after_success_returns_the_same_result_even_without_a_key()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var presign = await _fx.NewPresignAsync(userId);
        var body = new SignInput { Id = presign, SignedHash = "00" };

        var first = await client.SendAsync(Post("/api/sign", body));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A different signature over the same hash (ECDSA is randomised) is the same operation.
        var again = await client.SendAsync(Post("/api/sign", new SignInput { Id = presign, SignedHash = "01" }));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await ResultAsync(first), await ResultAsync(again));
        Assert.Equal(1, _fx.SignCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));

        // Raw PDF on replay too, for callers that ask for it.
        var raw = Post("/api/sign", body);
        raw.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        var rawResponse = await client.SendAsync(raw);
        Assert.Equal("application/pdf", rawResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Convert.FromBase64String((await ResultAsync(first))!), await rawResponse.Content.ReadAsByteArrayAsync());
    }

    [DockerFact]
    public async Task Concurrent_requests_with_one_key_do_the_work_once_and_charge_once()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };

        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        _fx.Timestamp = () =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            return SignedPdf;
        };

        var winner = client.SendAsync(Post("/api/timestamp", body, key));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
        var losers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.SendAsync(Post("/api/timestamp", body, key))));
        Assert.All(losers, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await winner).StatusCode);

        var replay = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, _fx.TimestampCalls);
        var usage = Assert.Single(await _fx.UsageAsync(userId));
        Assert.Equal(5 - usage.Count, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public async Task Credits_are_reserved_before_the_work_so_a_request_that_cannot_pay_never_starts_it()
    {
        // Balance for exactly one timestamp. On the old code both requests passed the
        // pre-check and both did the work; the second was refused only afterwards.
        var (userId, client) = await _fx.NewAccountAsync(credits: 1);
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };

        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        _fx.Timestamp = () =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            return SignedPdf;
        };

        var first = client.SendAsync(Post("/api/timestamp", body));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await _fx.BalanceAsync(userId));

        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var second = await client.SendAsync(Post("/api/timestamp", body), quick.Token);
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Contains("INSUFFICIENT_CREDITS", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, _fx.TimestampCalls);

        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(0, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));
    }

    [DockerFact]
    public async Task A_key_reused_for_a_different_request_is_refused()
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Post("/api/timestamp", new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() }, key));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var reused = await client.SendAsync(Post("/api/timestamp", new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect(), Reason = "other" }, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await reused.Content.ReadAsStringAsync());
        Assert.Equal(1, _fx.TimestampCalls);

        var unknown = await client.GetAsync($"/api/operations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [DockerFact]
    public async Task Parallel_begins_with_one_key_reserve_exactly_once()
    {
        var (userId, _) = await _fx.NewAccountAsync(credits: 10);
        var request = new OperationRequest(userId, Enterprise: false, "timestamp", Guid.NewGuid().ToString(),
            ApiOperationService.Fingerprint("x"), BaseCost: 1, Tier: 1);

        var begins = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _fx.Postgres.CreateContext();
            return await _fx.Service(db).BeginAsync(request);
        }));

        var started = Assert.Single(begins, b => b.Outcome == OperationStart.Started);
        Assert.All(begins.Where(b => b != started), b => Assert.Equal(OperationStart.InProgress, b.Outcome));
        Assert.Equal(9, await _fx.BalanceAsync(userId));

        await using (var db = _fx.Postgres.CreateContext())
        {
            var service = _fx.Service(db);
            Assert.True(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}", false, null)));
            // A second completion of the same attempt is a no-op, not a second charge.
            Assert.False(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}", false, null)));
            Assert.False(await service.FailAsync(started.Operation, started.Attempt, uncertain: false, "LATE"));
        }

        var replays = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _fx.Postgres.CreateContext();
            return await _fx.Service(db).BeginAsync(request);
        }));
        Assert.All(replays, b => Assert.Equal(OperationStart.Replay, b.Outcome));
        Assert.Equal(9, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));
    }

    [DockerFact]
    public async Task A_dead_attempt_returns_its_credits_and_cannot_complete_afterwards()
    {
        var (userId, _) = await _fx.NewAccountAsync(credits: 10);
        var request = new OperationRequest(userId, Enterprise: false, "seal", Guid.NewGuid().ToString(),
            ApiOperationService.Fingerprint("y"), BaseCost: 2, Tier: 1);

        OperationBegin started;
        await using (var db = _fx.Postgres.CreateContext())
        {
            started = await _fx.Service(db).BeginAsync(request);
        }
        Assert.Equal(OperationStart.Started, started.Outcome);
        Assert.Equal(8, await _fx.BalanceAsync(userId));

        // The worker died mid-signature; its lease runs out.
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.ApiOperations.Where(o => o.Id == started.Operation.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, await _fx.Service(db).SweepAsync(DateTimeOffset.UtcNow));
            var op = await db.ApiOperations.AsNoTracking().SingleAsync(o => o.Id == started.Operation.Id);
            Assert.Equal(ApiOperationState.FailedUncertain, op.State);
            Assert.Equal(ApiOperationService.LeaseExpiredCode, op.ErrorCode);
        }
        Assert.Equal(10, await _fx.BalanceAsync(userId));

        await using (var db = _fx.Postgres.CreateContext())
        {
            var service = _fx.Service(db);
            // The zombie finishing late must not deliver nor charge.
            Assert.False(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}", false, null)));
            var retry = await service.BeginAsync(request);
            Assert.Equal(OperationStart.Started, retry.Outcome);
            Assert.Equal(2, retry.Attempt);
        }
        Assert.Equal(8, await _fx.BalanceAsync(userId));
    }

    [Fact]
    public void Tsa_deadlines_nest_inside_the_portal_wait()
    {
        // Primary then backup must both fit well inside the portal's 60 s, leaving room for
        // the rest of /api/sign (#77: TSA 10 s + 10 s, endpoint 45 s, portal 60 s).
        Assert.True(PinnedIpTsaClient.RequestTimeout <= TimeSpan.FromSeconds(10));
        Assert.True(PinnedIpTsaClient.RequestTimeout * 2 < TimeSpan.FromSeconds(45));
    }
}
