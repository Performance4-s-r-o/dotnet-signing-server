using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    /// <summary>Short lease, fast heartbeat: a test can outlive the lease within seconds.</summary>
    public static readonly OperationsOptions Options = new()
    {
        Lease = TimeSpan.FromSeconds(2),
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        MaxStoredResultBytes = 20_000,
    };

    /// <summary>Makes storing a result fail (the one big bytea parameter), like a timeout would.</summary>
    public volatile bool FailResultStore;

    private readonly EphemeralDataProtectionProvider _dataProtection = new();

    private sealed class FailingResultStore(OperationStateFixture fx) : DbCommandInterceptor
    {
        private void Check(DbCommand command)
        {
            if (!fx.FailResultStore || !command.CommandText.Contains("\"ResultProtected\"", StringComparison.Ordinal)) return;
            if (command.Parameters.Cast<DbParameter>().Any(p => p.Value is byte[]))
            {
                throw new TimeoutException("simulated: storing the result timed out");
            }
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Check(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

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
            builder.UseSetting("Operations:Lease", Options.Lease.ToString());
            builder.UseSetting("Operations:HeartbeatInterval", Options.HeartbeatInterval.ToString());
            builder.UseSetting("Operations:MaxStoredResultBytes", Options.MaxStoredResultBytes.ToString());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();

                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<ApplicationDbContext>();
                var failStore = new FailingResultStore(fx);
                services.AddDbContext<ApplicationDbContext>(o =>
                {
                    fx.Postgres.Configure(o);
                    o.AddInterceptors(failStore);
                });

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
        FailResultStore = false;
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
        new(db, _dataProtection, NullLogger<ApiOperationService>.Instance, Microsoft.Extensions.Options.Options.Create(Options));

    public async Task<ApiOperation> OperationAsync(Guid userId)
    {
        await using var db = Postgres.CreateContext();
        return await db.ApiOperations.AsNoTracking().SingleAsync(o => o.UserId == userId);
    }

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
            Assert.True(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}"u8.ToArray(), false, null)));
            // Completing the same attempt again — what the retry strategy does after a commit
            // whose acknowledgement was lost — is success, and still not a second charge.
            Assert.True(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}"u8.ToArray(), false, null)));
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
            Assert.False(await service.CompleteAsync(started.Operation, started.Attempt, new StoredResult(200, "{}"u8.ToArray(), false, null)));
            var retry = await service.BeginAsync(request);
            Assert.Equal(OperationStart.Started, retry.Outcome);
            Assert.Equal(2, retry.Attempt);
            Assert.Equal(8, await _fx.BalanceAsync(userId));
            Assert.True(await service.FailAsync(retry.Operation, retry.Attempt, uncertain: false, "TEST_DONE"));
        }
    }

    [DockerFact]
    public async Task A_result_over_the_storage_limit_is_delivered_and_charged_but_not_kept()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        var big = Convert.ToBase64String(new byte[(int)OperationStateFixture.Options.MaxStoredResultBytes]);
        _fx.Timestamp = () => big;

        var first = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(big, await ResultAsync(first));
        Assert.Equal(4, await _fx.BalanceAsync(userId));

        var state = await StatusAsync(client, key);
        Assert.Equal("completed", state.GetProperty("state").GetString());
        Assert.False(state.GetProperty("resultAvailable").GetBoolean());

        var retry = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.Gone, retry.StatusCode);
        Assert.Contains("OPERATION_RESULT_EXPIRED", await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, _fx.TimestampCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public async Task A_failure_to_store_the_result_never_refunds_finished_work()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        _fx.FailResultStore = true;

        var response = await client.SendAsync(Post("/api/timestamp", new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() }, key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await ResultAsync(response));
        Assert.Equal(4, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));

        var op = await _fx.OperationAsync(userId);
        Assert.Equal(ApiOperationState.Completed, op.State);
        Assert.Null(op.ResultProtected);
    }

    [DockerFact]
    public async Task Without_a_key_the_result_is_not_kept()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);

        var response = await client.SendAsync(Post("/api/timestamp", new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Nobody can ask for it again, so a signed document is not parked in the database.
        var op = await _fx.OperationAsync(userId);
        Assert.Equal(ApiOperationState.Completed, op.State);
        Assert.Null(op.ResultProtected);
        Assert.Equal(1, op.CreditsCharged);
    }

    [DockerFact]
    public async Task A_result_past_its_retention_is_not_replayed_even_before_the_sweep()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/timestamp", body, key))).StatusCode);

        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.ApiOperations.Where(o => o.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ResultExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        Assert.False((await StatusAsync(client, key)).GetProperty("resultAvailable").GetBoolean());

        var retry = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.Gone, retry.StatusCode);
        Assert.Equal(1, _fx.TimestampCalls);
    }

    [DockerFact]
    public async Task A_signature_over_the_storage_limit_is_still_kept_for_the_retry()
    {
        // A presign is consumed once the signature completes; a lost result there would be a
        // paid signature nobody can fetch.
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var presign = await _fx.NewPresignAsync(userId);
        var big = Convert.ToBase64String(new byte[(int)OperationStateFixture.Options.MaxStoredResultBytes]);
        _fx.Sign = () => big;

        var first = await client.SendAsync(Post("/api/sign", new SignInput { Id = presign, SignedHash = "00" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var retry = await client.SendAsync(Post("/api/sign", new SignInput { Id = presign, SignedHash = "00" }));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(big, await ResultAsync(retry));
        Assert.Equal(1, _fx.SignCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public async Task A_stored_result_that_cannot_be_decrypted_is_410_not_500()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/timestamp", body, key))).StatusCode);

        // As if written by another build (purpose string) or under a lost key.
        await using (var db = _fx.Postgres.CreateContext())
        {
            await db.ApiOperations.Where(o => o.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ResultProtected, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        var retry = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.Gone, retry.StatusCode);
        Assert.Contains("OPERATION_RESULT_EXPIRED", await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, _fx.TimestampCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public async Task Expiry_rechecks_the_lease_so_a_renewed_attempt_survives()
    {
        var (userId, _) = await _fx.NewAccountAsync(credits: 10);
        var request = new OperationRequest(userId, Enterprise: false, "seal", Guid.NewGuid().ToString(),
            ApiOperationService.Fingerprint("z"), BaseCost: 1, Tier: 1);

        await using var db = _fx.Postgres.CreateContext();
        var service = _fx.Service(db);
        var started = await service.BeginAsync(request);
        await db.ApiOperations.Where(o => o.Id == started.Operation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));

        // The sweep reads the row as expired ...
        var stale = await db.ApiOperations.AsNoTracking().SingleAsync(o => o.Id == started.Operation.Id);
        // ... the heartbeat renews it in between ...
        Assert.True(await service.ExtendLeaseAsync(started.Operation.Id, started.Attempt));
        // ... so the expiry must not end the live attempt.
        Assert.False(await service.ExpireAsync(stale, DateTimeOffset.UtcNow));

        var row = await db.ApiOperations.AsNoTracking().SingleAsync(o => o.Id == started.Operation.Id);
        Assert.Equal(ApiOperationState.Running, row.State);
        Assert.Equal(9, await _fx.BalanceAsync(userId));

        // Leave no running row behind for the global sweep in other tests.
        Assert.True(await service.FailAsync(started.Operation, started.Attempt, uncertain: false, "TEST_DONE"));
    }

    [DockerFact]
    public async Task A_stored_result_is_bound_to_its_operation()
    {
        var (userA, _) = await _fx.NewAccountAsync(credits: 5);
        var (userB, _) = await _fx.NewAccountAsync(credits: 5);

        async Task<OperationBegin> CompletedAsync(Guid user, string json)
        {
            await using var db = _fx.Postgres.CreateContext();
            var service = _fx.Service(db);
            var begin = await service.BeginAsync(new OperationRequest(user, false, "seal", Guid.NewGuid().ToString(),
                ApiOperationService.Fingerprint(json), 1, 1));
            Assert.True(await service.CompleteAsync(begin.Operation, begin.Attempt,
                new StoredResult(200, System.Text.Encoding.UTF8.GetBytes(json), false, null)));
            return begin;
        }

        var a = await CompletedAsync(userA, "{\"result\":\"secret-of-a\"}");
        var b = await CompletedAsync(userB, "{\"result\":\"b\"}");

        await using var ctx = _fx.Postgres.CreateContext();
        var stolen = await ctx.ApiOperations.AsNoTracking().Where(o => o.Id == a.Operation.Id).Select(o => o.ResultProtected).SingleAsync();
        await ctx.ApiOperations.Where(o => o.Id == b.Operation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ResultProtected, stolen));
        var rowB = await ctx.ApiOperations.AsNoTracking().SingleAsync(o => o.Id == b.Operation.Id);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => _fx.Service(ctx).ReadResult(rowB));
    }

    [DockerFact]
    public async Task A_keyed_operation_that_outlives_its_lease_is_kept_alive_and_completes_once()
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

        var running = client.SendAsync(Post("/api/timestamp", body, key));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));

        // Twice the lease, with the sweep running all along: the heartbeat keeps it alive.
        var until = DateTime.UtcNow + OperationStateFixture.Options.Lease * 2;
        while (DateTime.UtcNow < until)
        {
            await using var db = _fx.Postgres.CreateContext();
            // The sweep is global: other tests' rows may expire, ours must not.
            await _fx.Service(db).SweepAsync(DateTimeOffset.UtcNow);
            Assert.Equal(ApiOperationState.Running, (await _fx.OperationAsync(userId)).State);
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Post("/api/timestamp", body, key))).StatusCode);

        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(1, _fx.TimestampCalls);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
        Assert.Single(await _fx.UsageAsync(userId));
    }

    // #77 owner decision: a retry after failed_uncertain may run the work again, but the
    // operationId is charged at most once (debits minus refunds == one price, one usage row).

    private static TsaCommunicationException TsaDown() =>
        new("https://tsa.example.com", "The operation has timed out.");

    private async Task AssertChargedOnceAsync(Guid userId, int startCredits)
    {
        var usage = Assert.Single(await _fx.UsageAsync(userId));
        Assert.Equal(startCredits - usage.Count, await _fx.BalanceAsync(userId));
        var op = await _fx.OperationAsync(userId);
        Assert.Equal(ApiOperationState.Completed, op.State);
        Assert.Equal(usage.Count, op.CreditsCharged);
        Assert.Equal(0, op.CreditsReserved);
    }

    [DockerFact]
    public async Task Retry_after_failed_uncertain_reruns_the_work_and_nets_exactly_one_charge()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        var calls = 0;
        _fx.Timestamp = () => ++calls == 1 ? throw TsaDown() : SignedPdf;

        var first = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(ApiOperationState.FailedUncertain, Header(first, ApiControllerBase.OperationStateHeader));
        Assert.Equal(5, await _fx.BalanceAsync(userId));
        Assert.Empty(await _fx.UsageAsync(userId));

        var retry = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, _fx.TimestampCalls);
        await AssertChargedOnceAsync(userId, 5);
    }

    [DockerFact]
    public async Task Retry_after_completed_is_a_replay_without_another_debit()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        var calls = 0;
        _fx.Timestamp = () => ++calls == 1 ? throw TsaDown() : SignedPdf;

        await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/timestamp", body, key))).StatusCode);
        var balance = await _fx.BalanceAsync(userId);

        var replay = await client.SendAsync(Post("/api/timestamp", body, key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Header(replay, "Idempotent-Replayed"));
        Assert.Equal(2, _fx.TimestampCalls);
        Assert.Equal(balance, await _fx.BalanceAsync(userId));
        await AssertChargedOnceAsync(userId, 5);
    }

    [DockerFact]
    public async Task Concurrent_retries_after_failed_uncertain_do_the_work_once_and_debit_once()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };

        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        _fx.Timestamp = () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw TsaDown();
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            return SignedPdf;
        };

        await client.SendAsync(Post("/api/timestamp", body, key));
        var attempts = Enumerable.Range(0, 2).Select(_ => client.SendAsync(Post("/api/timestamp", body, key))).ToList();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
        release.Set();
        var responses = await Task.WhenAll(attempts);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK
                                             && Header(r, "Idempotent-Replayed") != "true"));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(2, _fx.TimestampCalls);
        await AssertChargedOnceAsync(userId, 5);
    }

    [DockerFact]
    public async Task Two_failed_uncertain_retries_then_success_still_net_one_charge()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
        var calls = 0;
        _fx.Timestamp = () => ++calls <= 2 ? throw TsaDown() : SignedPdf;

        for (var i = 0; i < 2; i++)
        {
            var failed = await client.SendAsync(Post("/api/timestamp", body, key));
            Assert.Equal(ApiOperationState.FailedUncertain, Header(failed, ApiControllerBase.OperationStateHeader));
            Assert.Equal(5, await _fx.BalanceAsync(userId));
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/timestamp", body, key))).StatusCode);
        Assert.Equal(3, _fx.TimestampCalls);
        Assert.Equal(3, (await _fx.OperationAsync(userId)).Attempts);
        await AssertChargedOnceAsync(userId, 5);
    }

    // #72: deadline, uncertain vs. certain failure, no leaking of exception text.

    private OperationsOptions LiveOptions() =>
        _fx.App.Services.GetRequiredService<IOptions<OperationsOptions>>().Value;

    [DockerFact]
    public async Task Deadline_answers_504_fails_the_operation_and_returns_the_credits()
    {
        var options = LiveOptions();
        var original = options.SignDeadlineSeconds;
        options.SignDeadlineSeconds = 1;
        try
        {
            var (userId, client) = await _fx.NewAccountAsync(credits: 5);
            var key = Guid.NewGuid().ToString();
            var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
            // Synchronous and deaf to cancellation, like iText in the middle of a page.
            _fx.Timestamp = () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(8));
                return SignedPdf;
            };

            var started = DateTime.UtcNow;
            var response = await client.SendAsync(Post("/api/timestamp", body, key));
            var elapsed = DateTime.UtcNow - started;

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.True(elapsed < TimeSpan.FromSeconds(6), $"answered after {elapsed}");
            Assert.Contains("OPERATION_DEADLINE_EXCEEDED", await response.Content.ReadAsStringAsync());
            // Nothing external was reached: a plain failure, not an uncertain one.
            Assert.Equal(ApiOperationState.Failed, Header(response, ApiControllerBase.OperationStateHeader));
            Assert.Equal(5, await _fx.BalanceAsync(userId));
            Assert.Empty(await _fx.UsageAsync(userId));
            Assert.Equal("DEADLINE_EXCEEDED", (await StatusAsync(client, key)).GetProperty("errorCode").GetString());
        }
        finally
        {
            options.SignDeadlineSeconds = original;
        }
    }

    [DockerFact]
    public async Task Deadline_after_the_tsa_was_called_is_failed_uncertain()
    {
        var options = LiveOptions();
        var original = options.SignDeadlineSeconds;
        options.SignDeadlineSeconds = 1;
        try
        {
            var (userId, client) = await _fx.NewAccountAsync(credits: 5);
            var key = Guid.NewGuid().ToString();
            var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };
            _fx.Timestamp = () =>
            {
                OperationDeadline.MarkExternalCall(); // what the TSA client does before its request
                Thread.Sleep(TimeSpan.FromSeconds(6));
                return SignedPdf;
            };

            var response = await client.SendAsync(Post("/api/timestamp", body, key));

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal(ApiOperationState.FailedUncertain, Header(response, ApiControllerBase.OperationStateHeader));
            Assert.Equal("failed_uncertain", (await StatusAsync(client, key)).GetProperty("state").GetString());
            Assert.Equal(5, await _fx.BalanceAsync(userId));
        }
        finally
        {
            options.SignDeadlineSeconds = original;
        }
    }

    [DockerFact]
    public async Task Tsa_failure_response_carries_a_code_and_never_the_exception_text()
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 5);
        const string secret = "Connection refused (10.1.2.3:6379) internal-redis.corp";
        _fx.Timestamp = () => throw new TsaCommunicationException(
            "https://tsa.example.com", $"TSA at https://tsa.example.com is unreachable: {secret}");
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };

        var response = await client.SendAsync(Post("/api/timestamp", body, Guid.NewGuid().ToString()));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("TSA_UNREACHABLE", text);
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.DoesNotContain("internal-redis", text);
        Assert.DoesNotContain("Connection refused", text);
    }

    [DockerFact]
    public async Task Open_breaker_answers_503_with_retry_after_and_the_same_code()
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 5);
        _fx.Timestamp = () => throw new TsaCommunicationException(
            "https://tsa.example.com", "open", new TsaCircuitOpenException("tsa.example.com", TimeSpan.FromSeconds(17)));
        var body = new DocumentTimestampInput { PdfContent = Pdf, SignRect = new SignRect() };

        var response = await client.SendAsync(Post("/api/timestamp", body, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("17", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Contains("TSA_UNREACHABLE", await response.Content.ReadAsStringAsync());
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
