using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace DotNetSigningServer.Tests.Security;

/// <summary>find-codes rasterises with Ghostscript, which CI does not have; only the charge is under test.</summary>
internal sealed class NoScanUtilityController(
    ApplicationDbContext dbContext,
    IApiAuthService apiAuthService,
    Microsoft.Extensions.Logging.ILogger<PdfUtilityApiController> logger,
    ContentLimitGuard limitGuard,
    IOptions<BillingOptions> billingOptions,
    IWebHostEnvironment env,
    PdfTemplateService pdfTemplateService,
    PdfConversionService pdfConversionService,
    IStringLocalizer<SharedStrings> localizer)
    : PdfUtilityApiController(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, pdfConversionService, localizer)
{
    internal override Task<IReadOnlyList<object>> DetectCodesAsync(byte[] pdfBytes, CodeScanRaster.ScanPages pages, ScanPlan plan, CancellationToken ct, TimeSpan? budget = null) =>
        Task.FromResult<IReadOnlyList<object>>(Array.Empty<object>());
}

internal sealed class SwapUtilityController : IControllerActivator
{
    public object Create(ControllerContext context)
    {
        var type = context.ActionDescriptor.ControllerTypeInfo.AsType();
        if (type == typeof(PdfUtilityApiController)) type = typeof(NoScanUtilityController);
        return ActivatorUtilities.CreateInstance(context.HttpContext.RequestServices, type);
    }

    public void Release(ControllerContext context, object controller) => (controller as IDisposable)?.Dispose();
}

/// <summary>Theory twin of <see cref="DockerFactAttribute"/>.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (new DockerFactAttribute().Skip is { } skip) Skip = skip;
    }
}

/// <summary>
/// Issue #82: the result of the atomic debit must be honoured. The pre-check passes (balance 1),
/// but the balance is gone by the time of the debit — as with concurrent requests of one customer.
/// The endpoint must answer 402 and never hand out the result.
/// </summary>
public sealed class DebitRaceFixture : IAsyncLifetime
{
    public OutboxPostgresFixture Postgres { get; } = new();
    internal WebApplicationFactory<Program> App { get; private set; } = null!;
    public string ApiToken { get; private set; } = "";
    public Guid UserId { get; private set; }

    /// <summary>Sets the balance to zero right before the debit statement, like a request that won the race.</summary>
    private sealed class LoseTheRace(string connectionString) : DbCommandInterceptor
    {
        private const string DebitFragment = "\"CreditsRemaining\" = \"CreditsRemaining\" -";

        private void Race(DbCommand command)
        {
            if (!command.CommandText.Contains(DebitFragment, StringComparison.Ordinal)) return;
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var update = new NpgsqlCommand("UPDATE \"Users\" SET \"CreditsRemaining\" = 0", connection);
            update.ExecuteNonQuery();
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Race(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Race(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private sealed class NoEmail : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public Factory(string connectionString)
        {
            _connectionString = connectionString;
            ClientOptions.BaseAddress = new Uri("https://localhost");
            ClientOptions.AllowAutoRedirect = false;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("UseLocalDb", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
            builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();

                // Same context as the app, plus the interceptor that loses the race.
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<ApplicationDbContext>();
                var race = new LoseTheRace(_connectionString);
                services.AddDbContext<ApplicationDbContext>(o => o
                    .UseNpgsql(_connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "dotnet_signing"))
                    .AddInterceptors(race));

                // The work itself succeeds and returns a PDF; only the charge decides.
                var pdf = TestHelpers.CreateMinimalPdfBase64();
                var signing = new Mock<PdfSigningService>(null, null);
                signing.Setup(s => s.SignWithPfx(It.IsAny<PfxSignInput>())).Returns(pdf);
                signing.Setup(s => s.ApplyDocumentTimestamp(It.IsAny<DocumentTimestampInput>())).Returns(pdf);
                signing.Setup(s => s.ApplyVisualSign(It.IsAny<VisualSignInput>())).Returns(pdf);
                signing.Setup(s => s.AddAttachment(It.IsAny<AddAttachmentInput>())).Returns(pdf);
                services.RemoveAll<PdfSigningService>();
                services.AddScoped(_ => signing.Object);

                var conversion = new Mock<PdfConversionService>(NullLogger<PdfConversionService>.Instance);
                conversion.Setup(s => s.ConvertToPdfA(It.IsAny<ConvertToPdfAInput>())).Returns(pdf);
                services.RemoveAll<PdfConversionService>();
                services.AddScoped(_ => conversion.Object);

                services.AddSingleton<IControllerActivator, SwapUtilityController>();

                services.RemoveAll<PdfTemplateService>();
                services.AddScoped(sp =>
                {
                    var template = new Mock<PdfTemplateService>(
                        sp.GetRequiredService<ApplicationDbContext>(),
                        NullLogger<PdfTemplateService>.Instance,
                        sp.GetRequiredService<ContentLimitGuard>(),
                        Mock.Of<IStringLocalizerFactory>());
                    template.Setup(s => s.FillAsync(It.IsAny<FillPdfInput>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new FillPdfResponse { Files = { pdf } });
                    return template.Object;
                });
            });
        }
    }

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
        App = new Factory(Postgres.ConnectionString);
        _ = App.Server;

        await using var db = Postgres.CreateContext();
        var user = new User
        {
            Email = $"u{Guid.NewGuid():N}@example.com",
            EmailVerified = true,
            IsActive = true,
            SealAllowed = true,
            CreditsRemaining = 1,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        UserId = user.Id;

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
        ApiToken = plaintext;
    }

    /// <summary>Back to a balance of one: the pre-check passes again.</summary>
    public async Task ResetBalanceAsync()
    {
        await using var db = Postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"CreditsRemaining\" = 1");
    }

    public async Task<int> BalanceAsync()
    {
        await using var db = Postgres.CreateContext();
        return await db.Users.Where(u => u.Id == UserId).Select(u => u.CreditsRemaining).SingleAsync();
    }

    public async Task DisposeAsync()
    {
        if (App != null) await App.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

[Trait("Category", "Db")]
public class DebitResultTests : IClassFixture<DebitRaceFixture>
{
    private readonly DebitRaceFixture _fx;

    public DebitResultTests(DebitRaceFixture fx)
    {
        _fx = fx;
    }

    private static readonly string Pdf = TestHelpers.CreateMinimalPdfBase64();
    private static readonly SignRect Rect = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<string, string> Endpoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var (path, _) in Bodies())
        {
            data.Add(path, path);
        }
        return data;
    }

    private static IEnumerable<(string Path, object Body)> Bodies()
    {
        yield return ("/api/sign-pfx", new PfxSignInput { PdfContent = Pdf, PfxContent = "AAAA", PfxPassword = "x", SignRect = Rect });
        yield return ("/api/timestamp", new DocumentTimestampInput { PdfContent = Pdf, SignRect = Rect });
        yield return ("/api/visual-sign", new VisualSignInput { PdfContent = Pdf, SignRect = Rect });
        yield return ("/api/attachment", new AddAttachmentInput { PdfContent = Pdf, AttachmentContent = "AAAA", FileName = "a.txt" });
        yield return ("/api/convert/pdfa", new ConvertToPdfAInput { PdfContent = Pdf });
        yield return ("/api/fill-pdf", new FillPdfInput
        {
            PdfContent = Pdf,
            Fields = { new PdfFieldDefinition { FieldName = "f" } },
            Data = { new FillDataSet() },
        });
        yield return ("/api/find-codes", new FindCodesInput { PdfContent = Pdf });
    }

    [DockerTheory]
    [MemberData(nameof(Endpoints))]
    public async Task Failed_debit_is_402_and_the_result_is_not_delivered(string path, string _)
    {
        await _fx.ResetBalanceAsync();
        var body = Bodies().Single(b => b.Path == path).Body;
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fx.ApiToken);

        var response = await client.PostAsync(path, JsonContent.Create(body, body.GetType(), options: Json));
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.PaymentRequired,
            $"{path} answered {(int)response.StatusCode} instead of 402: {text}");
        Assert.DoesNotContain(Pdf, text);
        Assert.Equal(0, await _fx.BalanceAsync());
    }
}
