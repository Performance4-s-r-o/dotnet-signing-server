using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services;
using iText.Kernel.Pdf;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #184: <c>/api/presign</c> with <c>flattenForm</c> / <c>convertToPdfa</c>, both channels, without
/// Docker: the database is in memory and <c>/api/sign</c>'s <c>RunOperationAsync</c> is captured
/// for its price. Flattening, conversion and presign are real.
/// </summary>
public class PresignPdfaEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();
    private readonly User _user;

    public PresignPdfaEndpointTests()
    {
        _user = TestHelpers.CreateTestUser();
        _user.CreditsRemaining = 100;
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var path in _db.SigningData.Select(x => x.PresignedPdfPath).ToList())
        {
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class Harness : PdfSigningApiController
    {
        public readonly List<(string Operation, int BaseCost)> Calls = new();
        /// <summary>Run the paid work and answer its body (timestamp, visual-sign); otherwise only record the price.</summary>
        public bool RunWork { get; init; }

        public Harness(ApplicationDbContext db, LimitsOptions limits, BillingOptions billing, IStringLocalizer<SharedStrings> localizer)
            : base(db, new PdfSigningService(), null!, Mock.Of<IApiAuthService>(), NullLogger<PdfSigningApiController>.Instance,
                new ContentLimitGuard(TestHelpers.WrapOptions(limits)), TestHelpers.WrapOptions(billing),
                TestHelpers.CreateMockEnvironment("Testing"), null!, new EphemeralDataProtectionProvider(), localizer)
        {
        }

        protected override Task<IActionResult> RunOperationAsync(
            User user, string operation, string fingerprint, int baseCost,
            Func<CancellationToken, Task<PaidResult>> work, string? presignId = null, Func<Task>? afterCompleted = null)
        {
            Calls.Add((operation, baseCost));
            if (!RunWork) return Task.FromResult<IActionResult>(Ok());
            return RunAsync(work);
        }

        private async Task<IActionResult> RunAsync(Func<CancellationToken, Task<PaidResult>> work) =>
            Ok((await work(CancellationToken.None)).Body);
    }

    private Harness Controller(bool trusted = false, BillingOptions? billing = null, HttpContent? multipart = null, bool runWork = false)
    {
        var limits = new LimitsOptions { RejectActiveContent = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddSingleton(_db);
        services.AddSingleton(TestHelpers.WrapOptions(limits));
        services.AddSingleton(new PdfSafetyGuard(TestHelpers.WrapOptions(limits)));
        services.AddSingleton(new PdfConversionService(NullLogger<PdfConversionService>.Instance));
        services.AddSingleton(new ApiOperationService(_db, new EphemeralDataProtectionProvider(),
            NullLogger<ApiOperationService>.Instance, TestHelpers.WrapOptions(new OperationsOptions())));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _user.Id.ToString()) };
        if (trusted) claims.Add(new Claim(PortalTrust.TrustedCallerClaim, "true"));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
        if (multipart != null)
        {
            var body = multipart.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            http.Request.ContentType = multipart.Headers.ContentType!.ToString();
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = body.Length;
        }

        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string k, object[] _) => new LocalizedString(k, k));
        return new Harness(_db, limits, billing ?? new BillingOptions(), localizer.Object)
        {
            RunWork = runWork,
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static readonly string CertPem = TestHelpers.CreateTestCertificate().CertificatePem;

    private static PreSignInput Input(byte[] pdf, bool flatten = false, bool convert = false, string? conformance = null) => new()
    {
        PdfContent = Convert.ToBase64String(pdf),
        CertificatePem = CertPem,
        SignRect = new SignRect { X = 50, Y = 100, Width = 200, Height = 60 },
        FlattenForm = flatten,
        ConvertToPdfa = convert,
        PdfaConformance = conformance,
    };

    private static (int Status, string? Code, JsonElement Body) Describe(IActionResult result)
    {
        var status = (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? 200;
        var value = (result as ObjectResult)?.Value;
        var json = value == null ? "{}" : JsonSerializer.Serialize(value, value.GetType(), WebJson);
        var body = JsonDocument.Parse(json).RootElement.Clone();
        var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (status, code, body);
    }

    private static PdfAConformance? DeclaredIn(string path)
    {
        using var doc = new PdfDocument(new PdfReader(path));
        return doc.GetConformance().GetAConformance();
    }

    [Fact]
    public async Task Without_the_switches_presign_answers_and_stores_as_before()
    {
        var (status, _, body) = Describe(await Controller().PreSign(Input(PresignPdfaTests.Pdf(_ => { }))));

        Assert.Equal(200, status);
        Assert.Equal(new[] { "hashToSign", "id" }, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var row = await _db.SigningData.SingleAsync();
        Assert.Equal(0, row.PreparationCredits);
        Assert.Null(DeclaredIn(row.PresignedPdfPath));
    }

    [Fact]
    public async Task Without_the_switches_active_content_is_still_refused()
    {
        var (status, code, _) = Describe(await Controller().PreSign(Input(PresignPdfaTests.ScriptedFormEmbedded())));
        Assert.Equal(400, status);
        Assert.Equal("PDF_ACTIVE_CONTENT", code);
    }

    [Fact]
    public async Task Flatten_and_convert_in_one_presign_and_the_price_waits_for_the_signature()
    {
        var controller = Controller(billing: new BillingOptions { FlattenFormCredits = 1 });
        var (status, _, body) = Describe(await controller.PreSign(Input(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true)));

        Assert.Equal(200, status);
        var prep = body.GetProperty("preparation");
        Assert.Equal("flattened", prep.GetProperty("flatten").GetString());
        Assert.Equal("converted", prep.GetProperty("pdfa").GetString());
        Assert.Equal("PDF/A-2B", prep.GetProperty("conformance").GetString());
        Assert.Equal(2, prep.GetProperty("credits").GetInt32()); // flatten 1 + one page of PDF/A
        var row = await _db.SigningData.SingleAsync();
        Assert.Equal(2, row.PreparationCredits);
        Assert.Equal(PdfAConformance.PDF_A_2B, DeclaredIn(row.PresignedPdfPath));
        Assert.Equal(100, (await _db.Users.SingleAsync()).CreditsRemaining); // nothing charged by presign

        await controller.Sign(new SignInput { Id = row.Id, SignedHash = "00" });
        Assert.Equal(("sign", 3), controller.Calls.Single());
    }

    [Fact]
    public async Task The_trusted_caller_is_not_charged_for_the_preparation()
    {
        var controller = Controller(trusted: true, billing: new BillingOptions { FlattenFormCredits = 5 });
        var (status, _, body) = Describe(await controller.PreSign(Input(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true)));

        Assert.Equal(200, status);
        Assert.Equal(0, body.GetProperty("preparation").GetProperty("credits").GetInt32());
        var row = await _db.SigningData.SingleAsync();
        await controller.Sign(new SignInput { Id = row.Id, SignedHash = "00" });
        Assert.Equal(("sign", 1), controller.Calls.Single());
    }

    [Fact]
    public async Task A_document_that_cannot_be_converted_is_409_and_leaves_no_presign()
    {
        var (status, code, _) = Describe(await Controller().PreSign(Input(PresignPdfaTests.SymbolText(), convert: true)));
        Assert.Equal(409, status);
        Assert.Equal(DocumentPreparation.ConversionFailedCode, code);
        Assert.Empty(_db.SigningData);
    }

    [Fact]
    public async Task A_signed_document_that_is_not_PDFA_is_409()
    {
        var signed = PresignPdfaTests.SignedWithPfx(PresignPdfaTests.Pdf(_ => { }));
        var (status, code, _) = Describe(await Controller().PreSign(Input(signed, convert: true)));
        Assert.Equal(409, status);
        Assert.Equal("PDF_ALREADY_SIGNED", code);
    }

    [Fact]
    public async Task An_unsupported_conformance_is_400()
    {
        var (status, code, _) = Describe(await Controller().PreSign(Input(PresignPdfaTests.Pdf(_ => { }), convert: true, conformance: "PDF/A-1A")));
        Assert.Equal(400, status);
        Assert.Equal(DocumentPreparation.ConformanceUnsupportedCode, code);
    }

    [Fact]
    public async Task Too_few_credits_for_the_signature_and_preparation_is_402_at_presign()
    {
        (await _db.Users.SingleAsync()).CreditsRemaining = 1;
        await _db.SaveChangesAsync();

        var (status, code, _) = Describe(await Controller().PreSign(Input(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true)));
        Assert.Equal(402, status);
        Assert.Equal(ApiOperationService.InsufficientCreditsCode, code);
        Assert.Empty(_db.SigningData);
    }

    [Fact]
    public async Task Binary_channel_takes_the_same_switches()
    {
        var content = new MultipartFormDataContent();
        var metadata = new { certificatePem = CertPem, signRect = new { x = 50, y = 100, width = 200, height = 60 }, flattenForm = true, convertToPdfa = true, pdfaConformance = "PDF/A-3B" };
        content.Add(new StringContent(JsonSerializer.Serialize(metadata, WebJson), Encoding.UTF8, "application/json"), BinaryPdfChannel.MetadataPart);
        var part = new ByteArrayContent(PresignPdfaTests.ScriptedFormEmbedded());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");

        var (status, _, body) = Describe(await Controller(multipart: content).PreSignBinary());

        Assert.Equal(200, status);
        Assert.Equal("PDF/A-3B", body.GetProperty("preparation").GetProperty("conformance").GetString());
        Assert.Equal(PdfAConformance.PDF_A_3B, DeclaredIn((await _db.SigningData.SingleAsync()).PresignedPdfPath));
    }

    // ── timestamp and visual-sign: the same step (#184) ─────────────────────────────────────────

    private static VisualSignInput VisualInput(byte[] pdf, bool flatten = false, bool convert = false, string? conformance = null) => new()
    {
        PdfContent = Convert.ToBase64String(pdf),
        SignerName = "Jana Nováková",
        SignRect = new SignRect { X = 50, Y = 100, Width = 200, Height = 60 },
        FlattenForm = flatten,
        ConvertToPdfa = convert,
        PdfaConformance = conformance,
    };

    private static DocumentTimestampInput TimestampInput(byte[] pdf, bool flatten = false, bool convert = false, string? conformance = null) => new()
    {
        PdfContent = Convert.ToBase64String(pdf),
        SignRect = new SignRect { X = 50, Y = 100, Width = 200, Height = 60 },
        TsaUrl = "https://tsa.invalid/",
        FlattenForm = flatten,
        ConvertToPdfa = convert,
        PdfaConformance = conformance,
    };

    [Fact]
    public async Task Visual_sign_prepares_draws_into_the_PDFA_and_adds_the_preparation_to_its_price()
    {
        var controller = Controller(billing: new BillingOptions { FlattenFormCredits = 1 }, runWork: true);
        var (status, _, body) = Describe(await controller.ApplyVisualSign(VisualInput(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true, conformance: "PDF/A-3B")));

        Assert.Equal(200, status);
        var prep = body.GetProperty("preparation");
        Assert.Equal("flattened", prep.GetProperty("flatten").GetString());
        Assert.Equal("converted", prep.GetProperty("pdfa").GetString());
        Assert.Equal("PDF/A-3B", prep.GetProperty("conformance").GetString());
        Assert.Equal(2, prep.GetProperty("credits").GetInt32());
        Assert.Equal(("visual-sign", 3), controller.Calls.Single());
        var result = Convert.FromBase64String(body.GetProperty("result").GetString()!);
        Assert.Empty(PdfAConformanceCheck.Problems(result, PdfAConformance.PDF_A_3B));
    }

    [Fact]
    public async Task Visual_sign_without_the_switches_answers_as_before()
    {
        var controller = Controller(runWork: true);
        var (status, _, body) = Describe(await controller.ApplyVisualSign(VisualInput(PresignPdfaTests.Pdf(_ => { }))));

        Assert.Equal(200, status);
        Assert.Equal(new[] { "result" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("visual-sign", 1), controller.Calls.Single());
    }

    [Fact]
    public async Task Timestamp_takes_the_preparation_into_its_reservation_and_trusted_caller_pays_only_the_timestamp()
    {
        var paying = Controller(billing: new BillingOptions { FlattenFormCredits = 1 });
        Assert.Equal(200, Describe(await paying.ApplyTimestamp(TimestampInput(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true))).Status);
        Assert.Equal(("timestamp", 3), paying.Calls.Single());

        var trusted = Controller(trusted: true, billing: new BillingOptions { FlattenFormCredits = 5 });
        await trusted.ApplyTimestamp(TimestampInput(PresignPdfaTests.ScriptedFormEmbedded(), flatten: true, convert: true));
        Assert.Equal(("timestamp", 1), trusted.Calls.Single());
    }

    [Fact]
    public async Task Timestamp_and_visual_sign_refuse_like_presign_before_any_reservation()
    {
        var withFile = PresignPdfaTests.Pdf(doc => doc.AddFileAttachment("a.txt",
            iText.Kernel.Pdf.Filespec.PdfFileSpec.CreateEmbeddedFileSpec(doc, new byte[] { 1 }, "a", "a.txt", null, null, null)));
        var cases = new (Func<Harness, Task<IActionResult>> Call, int Status, string Code)[]
        {
            (c => c.ApplyTimestamp(TimestampInput(PresignPdfaTests.SymbolText(), convert: true)), 409, DocumentPreparation.ConversionFailedCode),
            (c => c.ApplyVisualSign(VisualInput(PresignPdfaTests.SymbolText(), convert: true)), 409, DocumentPreparation.ConversionFailedCode),
            (c => c.ApplyVisualSign(VisualInput(withFile, convert: true)), 409, DocumentPreparation.EmbeddedFilesCode),
            (c => c.ApplyVisualSign(VisualInput(PresignPdfaTests.Pdf(_ => { }), convert: true, conformance: "PDF/A-1B")), 400, DocumentPreparation.ConformanceUnsupportedCode),
            (c => c.ApplyTimestamp(TimestampInput(PdfFormFlattenerTests.DuplicateNamedFields(), flatten: true)), 409, PdfFormFlattener.FormInvalidCode),
            // Active content without flattening is still refused at the door.
            (c => c.ApplyVisualSign(VisualInput(PresignPdfaTests.ScriptedFormEmbedded(), convert: true)), 400, "PDF_ACTIVE_CONTENT"),
        };
        foreach (var (call, expectedStatus, expectedCode) in cases)
        {
            var controller = Controller();
            var (status, code, _) = Describe(await call(controller));
            Assert.Equal((expectedStatus, expectedCode), (status, code));
            Assert.Empty(controller.Calls);
        }
    }

    [Fact]
    public async Task A_completed_operation_under_the_same_key_is_not_prepared_again()
    {
        (await _db.Users.SingleAsync()).CreditsRemaining = 0;
        await _db.SaveChangesAsync();
        var key = Guid.NewGuid().ToString();
        _db.ApiOperations.Add(new ApiOperation
        {
            UserId = _user.Id,
            OperationId = key,
            ClientKey = true,
            Operation = "visual-sign",
            State = ApiOperationState.Completed,
            RequestFingerprint = "x",
        });
        await _db.SaveChangesAsync();
        var controller = Controller();
        controller.HttpContext.Request.Headers[PdfSigningApiController.IdempotencyKeyHeader] = key;

        // A document that would fail the conversion: a replay must reach the stored answer instead.
        await controller.ApplyVisualSign(VisualInput(PresignPdfaTests.SymbolText(), convert: true));
        Assert.Equal(("visual-sign", 1), controller.Calls.Single());
    }
}
