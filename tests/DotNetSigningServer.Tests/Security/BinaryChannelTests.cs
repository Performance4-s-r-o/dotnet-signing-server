using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// Aplikace proti PostgreSQL pro interní binární kanál (#74). Presign běží doopravdy (jen
/// soubor v /tmp), podpis a sken kódů jsou podvržené: tady se zkouší kanál, ne podpis.
/// </summary>
public sealed class BinaryChannelFixture : IAsyncLifetime
{
    public const long PdfMaxBytes = 200_000;
    public const int PdfMaxPages = 5;
    public const int MaxPendingPresigns = 2;

    public OutboxPostgresFixture Postgres { get; } = new();
    internal WebApplicationFactory<Program> App { get; private set; } = null!;
    public string CertificatePem { get; } = TestHelpers.CreateTestCertificate().CertificatePem;

    /// <summary>Co vrátí /api/sign (base64), jako by se podpis podařil.</summary>
    public static readonly byte[] SignedPdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

    private sealed class NoEmail : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }

    private sealed class Factory(BinaryChannelFixture fx) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ClientOptions.BaseAddress = new Uri("https://localhost");
            builder.UseEnvironment("Testing");
            builder.UseSetting("UseLocalDb", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", fx.Postgres.ConnectionString);
            builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
            builder.UseSetting("Limits:PdfMaxBytes", PdfMaxBytes.ToString());
            builder.UseSetting("Limits:PdfMaxPages", PdfMaxPages.ToString());
            builder.UseSetting("Limits:MaxPendingPresignsPerUser", MaxPendingPresigns.ToString());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();

                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(o => fx.Postgres.Configure(o));

                // HandlePreSign není virtuální: běží skutečný kód, jen HandleSign je podvržený.
                var signing = new Mock<PdfSigningService>(null, null, null);
                signing.Setup(s => s.HandleSign(
                        It.IsAny<SignInput>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TsaEndpoint?>()))
                    .Returns(() => Convert.ToBase64String(SignedPdf));
                services.RemoveAll<PdfSigningService>();
                services.AddScoped(_ => signing.Object);

                // Ghostscript v CI není; sken nic nenajde, účtuje se za stránky jako doopravdy.
                services.AddSingleton<IControllerActivator, SwapUtilityController>();
            });
        }
    }

    public async Task InitializeAsync()
    {
        var keys = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
        App = new Factory(this);
        _ = App.Server;
    }

    /// <summary>Nový účet s vlastním tokenem, ať testy nesdílejí zůstatek ani rozpracované presigny.</summary>
    public async Task<(Guid UserId, HttpClient Client)> NewAccountAsync(int credits)
    {
        await using var db = Postgres.CreateContext();
        var user = new User
        {
            Email = $"u{Guid.NewGuid():N}@example.com",
            EmailVerified = true,
            IsActive = true,
            CreditsRemaining = credits,
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

    public async Task<int> BalanceAsync(Guid userId)
    {
        await using var db = Postgres.CreateContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.CreditsRemaining).SingleAsync();
    }

    public async Task<SigningData?> PresignAsync(string id)
    {
        await using var db = Postgres.CreateContext();
        return await db.SigningData.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task DisposeAsync()
    {
        if (App != null) await App.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

/// <summary>
/// #74: interní binární kanál (multipart/form-data dovnitř, <c>Accept: application/pdf</c> ven)
/// musí projít STEJNÝMI kontrolami jako JSON s base64 — ověření, LimitGuard, SafetyGuard, limit
/// rozpracovaných presignů, rezervace a účtování, idempotence. Každý scénář běží oběma kanály.
/// </summary>
[Trait("Category", "Db")]
public class BinaryChannelTests : IClassFixture<BinaryChannelFixture>
{
    private readonly BinaryChannelFixture _fx;

    public BinaryChannelTests(BinaryChannelFixture fx) => _fx = fx;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Channels => new() { "json", "binary" };

    private static byte[] Pdf(int pages = 1, Action<PdfDocument>? extra = null, int noiseBytes = 0)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms, new WriterProperties().SetCompressionLevel(0))))
        {
            for (var i = 0; i < pages; i++) doc.AddNewPage(PageSize.A4);
            if (noiseBytes > 0)
            {
                // Náhodná data se nezkomprimují: soubor je opravdu tak velký.
                var data = new byte[noiseBytes];
                new Random(7).NextBytes(data);
                doc.GetFirstPage().GetPdfObject().Put(new PdfName("Noise"), new PdfStream(data));
            }
            extra?.Invoke(doc);
        }
        return ms.ToArray();
    }

    /// <summary>Stejný požadavek, jednou jako dnešní JSON s base64, jednou binárně.</summary>
    private static HttpRequestMessage Request(string channel, string path, object metadata, byte[] pdf, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (channel == "json")
        {
            var body = JsonSerializer.SerializeToNode(metadata, metadata.GetType(), WebJson)!.AsObject();
            body["pdfContent"] = Convert.ToBase64String(pdf);
            request.Content = JsonContent.Create(body);
        }
        else
        {
            request.Content = Multipart(metadata, pdf);
        }
        if (key != null) request.Headers.Add(ApiControllerBase.IdempotencyKeyHeader, key);
        return request;
    }

    private static MultipartFormDataContent Multipart(object? metadata, byte[]? pdf, Action<MultipartFormDataContent>? extra = null)
    {
        var content = new MultipartFormDataContent();
        if (metadata != null)
        {
            var json = metadata as string ?? JsonSerializer.Serialize(metadata, metadata.GetType(), WebJson);
            content.Add(new StringContent(json, Encoding.UTF8, "application/json"), BinaryPdfChannel.MetadataPart);
        }
        if (pdf != null)
        {
            var part = new ByteArrayContent(pdf);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");
        }
        extra?.Invoke(content);
        return content;
    }

    private object PresignMetadata() => new
    {
        certificatePem = _fx.CertificatePem,
        reason = "test",
        location = "Praha",
        signRect = new { x = 50, y = 50, width = 200, height = 60 },
        signPageNumber = 1,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var c) ? c.GetString() : null;

    // ── presign ──────────────────────────────────────────────────────────

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_succeeds_and_stores_the_presign(string channel)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);

        var response = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var id = body.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]+$", body.GetProperty("hashToSign").GetString());
        var row = await _fx.PresignAsync(id);
        Assert.NotNull(row);
        Assert.Equal(userId, row!.UserId);
        Assert.True(File.Exists(row.PresignedPdfPath));
        // Presign se neúčtuje — v žádném kanálu.
        Assert.Equal(5, await _fx.BalanceAsync(userId));
    }

    public static TheoryData<string, string> RefusedPdfs()
    {
        var data = new TheoryData<string, string>();
        foreach (var channel in new[] { "json", "binary" })
        {
            data.Add(channel, "PDF_INVALID");
            data.Add(channel, "PDF_SIZE_EXCEEDED");
            data.Add(channel, "PDF_TOO_MANY_PAGES");
            data.Add(channel, "PDF_ACTIVE_CONTENT");
        }
        return data;
    }

    private static byte[] RefusedPdf(string code) => code switch
    {
        "PDF_INVALID" => "this is not a pdf"u8.ToArray(),
        "PDF_SIZE_EXCEEDED" => Pdf(noiseBytes: (int)BinaryChannelFixture.PdfMaxBytes + 10_000),
        "PDF_TOO_MANY_PAGES" => Pdf(pages: BinaryChannelFixture.PdfMaxPages + 1),
        "PDF_ACTIVE_CONTENT" => Pdf(extra: doc => doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript("app.alert('hi')"))),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    [DockerTheory]
    [MemberData(nameof(RefusedPdfs))]
    public async Task Presign_refuses_what_the_guards_refuse(string channel, string code)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);

        var response = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), RefusedPdf(code)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal(5, await _fx.BalanceAsync(userId));
    }

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_refuses_an_oversized_signature_image(string channel)
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 5);
        var metadata = new
        {
            certificatePem = _fx.CertificatePem,
            signRect = new { x = 50, y = 50, width = 200, height = 60 },
            signImageContent = Convert.ToBase64String(new byte[2 * 1024 * 1024]),
        };

        var response = await client.SendAsync(Request(channel, "/api/presign", metadata, Pdf()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("IMAGE_SIZE_EXCEEDED", await CodeAsync(response));
    }

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_without_credits_is_402(string channel)
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 0);

        var response = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_without_a_token_is_401(string channel)
    {
        var client = _fx.App.CreateClient();

        var response = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_keeps_the_limit_of_pending_presigns(string channel)
    {
        var (_, client) = await _fx.NewAccountAsync(credits: 5);
        for (var i = 0; i < BinaryChannelFixture.MaxPendingPresigns; i++)
        {
            var ok = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var refused = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("TOO_MANY_PENDING_PRESIGNS", await CodeAsync(refused));
    }

    // ── sign: PDF jen ven ────────────────────────────────────────────────

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Presign_then_sign_delivers_the_same_document_as_json_or_raw_pdf(string channel)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var presign = await client.SendAsync(Request(channel, "/api/presign", PresignMetadata(), Pdf()));
        var id = (await JsonAsync(presign)).GetProperty("id").GetString()!;

        var sign = new HttpRequestMessage(HttpMethod.Post, "/api/sign")
        {
            Content = JsonContent.Create(new SignInput { Id = id, SignedHash = "00" }),
        };
        if (channel == "binary") sign.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        var response = await client.SendAsync(sign);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signed = channel == "binary"
            ? await response.Content.ReadAsByteArrayAsync()
            : Convert.FromBase64String((await JsonAsync(response)).GetProperty("result").GetString()!);
        if (channel == "binary") Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(BinaryChannelFixture.SignedPdf, signed);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
        Assert.Null(await _fx.PresignAsync(id));

        // Opakování téhož podpisu: z uloženého výsledku, bez druhého účtování, v témž tvaru.
        var again = new HttpRequestMessage(HttpMethod.Post, "/api/sign")
        {
            Content = JsonContent.Create(new SignInput { Id = id, SignedHash = "00" }),
        };
        if (channel == "binary") again.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        var replay = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(ApiControllerBase.ReplayedHeader));
        var replayed = channel == "binary"
            ? await replay.Content.ReadAsByteArrayAsync()
            : Convert.FromBase64String((await JsonAsync(replay)).GetProperty("result").GetString()!);
        Assert.Equal(BinaryChannelFixture.SignedPdf, replayed);
        Assert.Equal(4, await _fx.BalanceAsync(userId));
    }

    // ── find-codes ───────────────────────────────────────────────────────

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Find_codes_charges_by_pages_and_replays_with_the_same_key(string channel)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var key = Guid.NewGuid().ToString();
        var pdf = Pdf(pages: 4);

        var first = await client.SendAsync(Request(channel, "/api/find-codes", new { codeType = "qr" }, pdf, key));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await JsonAsync(first);
        Assert.Equal(4, body.GetProperty("pages").GetInt32());
        Assert.Equal(2, body.GetProperty("credits").GetInt32());
        Assert.Equal(3, await _fx.BalanceAsync(userId));

        var replay = await client.SendAsync(Request(channel, "/api/find-codes", new { codeType = "qr" }, pdf, key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(ApiControllerBase.ReplayedHeader));
        Assert.Equal(3, await _fx.BalanceAsync(userId));

        // Jiný dokument pod týmž klíčem je jiný požadavek — i když se metadata shodují.
        var other = await client.SendAsync(Request(channel, "/api/find-codes", new { codeType = "qr" }, Pdf(pages: 2), key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await CodeAsync(other));
        Assert.Equal(3, await _fx.BalanceAsync(userId));
    }

    [DockerTheory]
    [MemberData(nameof(Channels))]
    public async Task Find_codes_without_enough_credits_is_402_before_any_work(string channel)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 1);

        var response = await client.SendAsync(Request(channel, "/api/find-codes", new { codeType = "any" }, Pdf(pages: 4)));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal(1, await _fx.BalanceAsync(userId));
    }

    [DockerTheory]
    [MemberData(nameof(RefusedPdfs))]
    public async Task Find_codes_refuses_what_the_guards_refuse(string channel, string code)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);

        var response = await client.SendAsync(Request(channel, "/api/find-codes", new { codeType = "any" }, RefusedPdf(code)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal(5, await _fx.BalanceAsync(userId));
    }

    // ── jen binární kanál: tvar těla ─────────────────────────────────────

    public static TheoryData<string> MalformedBodies => new()
    {
        "no-pdf", "no-metadata", "pdf-twice", "unknown-part", "inline-pdf", "metadata-not-json", "pdf-wrong-type",
    };

    [DockerTheory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Binary_body_of_the_wrong_shape_is_400_multipart_invalid(string shape)
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);
        var pdf = Pdf();
        var metadata = new { codeType = "any" };
        MultipartFormDataContent content = shape switch
        {
            "no-pdf" => Multipart(metadata, null),
            "no-metadata" => Multipart(null, pdf),
            "pdf-twice" => Multipart(metadata, pdf, c => c.Add(new ByteArrayContent(pdf), BinaryPdfChannel.PdfPart, "b.pdf")),
            "unknown-part" => Multipart(metadata, pdf, c => c.Add(new StringContent("x"), "extra")),
            "inline-pdf" => Multipart(new { codeType = "any", pdfContent = Convert.ToBase64String(pdf) }, pdf),
            "metadata-not-json" => Multipart("{not json", pdf),
            "pdf-wrong-type" => Multipart(metadata, null, c =>
            {
                var part = new ByteArrayContent(pdf);
                part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                c.Add(part, BinaryPdfChannel.PdfPart, "a.pdf");
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var response = await client.PostAsync("/api/find-codes", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BinaryPdfChannel.InvalidCode, await CodeAsync(response));
        Assert.Equal(5, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public async Task Endpoints_without_the_binary_channel_still_answer_415_to_multipart()
    {
        var (userId, client) = await _fx.NewAccountAsync(credits: 5);

        var response = await client.PostAsync("/api/timestamp", Multipart(new { tsaUrl = "https://tsa.example.com" }, Pdf()));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(5, await _fx.BalanceAsync(userId));
    }

    [DockerFact]
    public void Binary_channel_is_not_in_the_public_openapi()
    {
        var explorer = _fx.App.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        var routes = explorer.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(d => d.HttpMethod == "POST" && d.RelativePath is "api/presign" or "api/find-codes")
            .ToList();

        // Jen JSON varianty; binární akce by Swaggeru navíc udělala konflikt dvou akcí na jedné cestě.
        Assert.Equal(2, routes.Count);
        Assert.DoesNotContain(routes, d => d.SupportedRequestFormats.Any(f => f.MediaType.StartsWith("multipart/", StringComparison.Ordinal)));
    }
}

/// <summary>Čtení části <c>pdf</c> s limitem (#74): bez Dockeru.</summary>
public class BinaryPdfChannelReadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(10L)]
    [InlineData(1_000_000L)]
    public async Task Reads_exactly_up_to_the_limit_whatever_the_size_hint(long? hint)
    {
        var data = new byte[100_000];
        new Random(1).NextBytes(data);

        var read = await BinaryPdfChannel.ReadBoundedAsync(new MemoryStream(data), hint, max: data.Length, CancellationToken.None);

        Assert.Equal(data, read);
    }

    [Fact]
    public async Task One_byte_over_the_limit_is_pdf_size_exceeded()
    {
        var ex = await Assert.ThrowsAsync<DotNetSigningServer.Exceptions.ApiValidationException>(() =>
            BinaryPdfChannel.ReadBoundedAsync(new MemoryStream(new byte[100_001]), 1_000_000, max: 100_000, CancellationToken.None));

        Assert.Equal("PDF_SIZE_EXCEEDED", ex.Code);
    }

    [Fact]
    public async Task Not_multipart_is_multipart_invalid()
    {
        var ex = await Assert.ThrowsAsync<DotNetSigningServer.Exceptions.ApiValidationException>(() =>
            BinaryPdfChannel.ReadAsync<FindCodesInput>("application/json", new MemoryStream(), null,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), 1000, CancellationToken.None));

        Assert.Equal(BinaryPdfChannel.InvalidCode, ex.Code);
    }
}
