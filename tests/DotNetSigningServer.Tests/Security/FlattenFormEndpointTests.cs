using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #180: <c>/api/flatten-form</c> in both channels, without Docker or a database: the controller is
/// called directly and <c>RunOperationAsync</c> (operation state in PostgreSQL) is captured by a
/// test subclass, which records the price. The flattening itself is real.
/// </summary>
public class FlattenFormEndpointTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record Call(string Operation, string Fingerprint, int BaseCost);

    private sealed class Harness : PdfUtilityApiController
    {
        public readonly List<Call> Calls = new();

        public Harness(ApplicationDbContext db, ContentLimitGuard limits, PdfTemplateService templates,
            IStringLocalizer<SharedStrings> localizer, BillingOptions billing)
            : base(db, new Mock<IApiAuthService>().Object, NullLogger<PdfUtilityApiController>.Instance, limits,
                TestHelpers.WrapOptions(billing), TestHelpers.CreateMockEnvironment(), templates,
                new PdfConversionService(NullLogger<PdfConversionService>.Instance), localizer)
        {
        }

        protected override async Task<IActionResult> RunOperationAsync(
            User user, string operation, string fingerprint, int baseCost,
            Func<CancellationToken, Task<PaidResult>> work, string? presignId = null, Func<Task>? afterCompleted = null)
        {
            Calls.Add(new Call(operation, fingerprint, baseCost));
            var result = await work(CancellationToken.None);
            return Ok(result.Body);
        }
    }

    private static Harness Controller(HttpContent? multipart = null, BillingOptions? billing = null, bool trusted = false, int maxPages = 5)
    {
        var limitOptions = new LimitsOptions { PdfMaxPages = maxPages, RejectActiveContent = true };
        var limits = new ContentLimitGuard(TestHelpers.WrapOptions(limitOptions));
        var db = TestHelpers.CreateInMemoryDbContext();
        var user = TestHelpers.CreateTestUser();
        user.CreditsRemaining = 100;
        db.Users.Add(user);
        db.SaveChanges();

        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
        var templates = new PdfTemplateService(db, NullLogger<PdfTemplateService>.Instance, limits, new Mock<IStringLocalizerFactory>().Object);

        var services = new ServiceCollection()
            .AddSingleton(new PdfSafetyGuard(TestHelpers.WrapOptions(limitOptions)))
            .AddSingleton(TestHelpers.WrapOptions(limitOptions))
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()))
            .BuildServiceProvider();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()) };
        if (trusted) claims.Add(new Claim(PortalTrust.TrustedCallerClaim, "true"));
        var http = new DefaultHttpContext { RequestServices = services };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
        if (multipart != null)
        {
            var body = multipart.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            http.Request.ContentType = multipart.Headers.ContentType!.ToString();
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = body.Length;
        }

        return new Harness(db, limits, templates, localizer.Object, billing ?? new BillingOptions())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static MultipartFormDataContent Multipart(byte[] pdf)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("{}", Encoding.UTF8, "application/json"), BinaryPdfChannel.MetadataPart);
        var part = new ByteArrayContent(pdf);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");
        return content;
    }

    private static byte[] Pdf(Action<PdfDocument>? configure = null, int pages = 1)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            for (var i = 0; i < pages; i++) doc.AddNewPage(PageSize.A4);
            configure?.Invoke(doc);
        }
        return ms.ToArray();
    }

    /// <summary>A filled form with a document-level script: refused by every signing endpoint today.</summary>
    private static byte[] ScriptedForm() => Pdf(doc =>
    {
        var form = PdfAcroForm.GetAcroForm(doc, true);
        var field = new TextFormFieldBuilder(doc, "name").SetWidgetRectangle(new Rectangle(50, 700, 300, 24)).SetPage(1).CreateText();
        field.SetValue("Jane Roe");
        form.AddField(field);
        doc.GetCatalog().GetNameTree(PdfName.JavaScript).AddEntry("init", PdfAction.CreateJavaScript("app.alert('hi')").GetPdfObject());
    });

    private static (int Status, string? Code, JsonElement Body) Describe(IActionResult result)
    {
        var status = (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? 200;
        var value = (result as ObjectResult)?.Value;
        var json = value == null ? "{}" : JsonSerializer.Serialize(value, value.GetType(), WebJson);
        var body = JsonDocument.Parse(json).RootElement.Clone();
        var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) ? c.GetString() : null;
        return (status, code, body);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Json_flattens_and_answers_both_hashes_and_what_was_removed()
    {
        var pdf = ScriptedForm();
        var controller = Controller();

        var (status, _, body) = Describe(await controller.FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(pdf) }));

        Assert.Equal(200, status);
        var result = Convert.FromBase64String(body.GetProperty("result").GetString()!);
        Assert.Equal(Sha256(pdf), body.GetProperty("sourceSha256").GetString());
        Assert.Equal(Sha256(result), body.GetProperty("resultSha256").GetString());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.Equal(1, body.GetProperty("flattenedFields").GetInt32());
        Assert.Equal(new[] { "document-javascript" }, body.GetProperty("removed").EnumerateArray().Select(e => e.GetString()).ToArray());
        // The result goes through the same guard as every signing endpoint, active content included.
        new PdfSafetyGuard(TestHelpers.WrapOptions(new LimitsOptions { RejectActiveContent = true })).EnsureSafePdf(result, "test");

        var call = Assert.Single(controller.Calls);
        Assert.Equal("flatten-form", call.Operation);
        Assert.Equal(1, call.BaseCost);
    }

    [Fact]
    public async Task Binary_returns_the_same_result_as_json()
    {
        var pdf = ScriptedForm();

        var json = Describe(await Controller().FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(pdf) }));
        var binaryController = Controller(Multipart(pdf));
        var binary = Describe(await binaryController.FlattenFormBinary());

        Assert.Equal(200, binary.Status);
        // The rewritten file differs run to run (trailer /ID, dates); everything else is the same.
        foreach (var key in new[] { "sourceSha256", "changed", "flattenedFields", "removed" })
        {
            Assert.Equal(json.Body.GetProperty(key).GetRawText(), binary.Body.GetProperty(key).GetRawText());
        }
        Assert.Equal(1, Assert.Single(binaryController.Calls).BaseCost);
    }

    [Fact]
    public async Task Signed_document_is_409_PDF_ALREADY_SIGNED()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var signed = new PdfSigningService(TestHelpers.WrapOptions(new EvidenceOptions()), new PdfVisualSigningService())
            .SignWithPfx(new PfxSignInput
            {
                PdfContent = TestHelpers.CreateMinimalPdfBase64(),
                PfxContent = pfxBase64,
                PfxPassword = password,
                FieldName = "Signature1",
                SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            });

        var json = Describe(await Controller().FlattenForm(new FlattenFormInput { PdfContent = signed }));
        var binary = Describe(await Controller(Multipart(Convert.FromBase64String(signed))).FlattenFormBinary());

        foreach (var (status, code, _) in new[] { json, binary })
        {
            Assert.Equal(409, status);
            Assert.Equal("PDF_ALREADY_SIGNED", code);
        }
    }

    [Fact]
    public async Task Dynamic_xfa_is_409_PDF_XFA_DYNAMIC()
    {
        var pdf = Pdf(doc =>
        {
            var xfa = new PdfStream(Encoding.UTF8.GetBytes(
                "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/></xdp:xdp>"));
            PdfAcroForm.GetAcroForm(doc, true).GetPdfObject().Put(PdfName.XFA, xfa.MakeIndirect(doc));
            doc.GetCatalog().Put(new PdfName("NeedsRendering"), PdfBoolean.TRUE);
        });

        var (status, code, _) = Describe(await Controller().FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(pdf) }));

        Assert.Equal(409, status);
        Assert.Equal("PDF_XFA_DYNAMIC", code);
    }

    [Fact]
    public async Task Structural_limits_still_refuse_with_400_before_any_work()
    {
        var pdf = Pdf(pages: 3);

        var controller = Controller(maxPages: 2);
        var (status, code, _) = Describe(await controller.FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(pdf) }));
        var binaryController = Controller(Multipart(pdf), maxPages: 2);
        var binary = Describe(await binaryController.FlattenFormBinary());

        Assert.Equal((400, "PDF_TOO_MANY_PAGES"), (status, code));
        Assert.Equal((400, "PDF_TOO_MANY_PAGES"), (binary.Status, binary.Code));
        Assert.Empty(controller.Calls);
        Assert.Empty(binaryController.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Price_comes_from_the_configuration(int credits)
    {
        var controller = Controller(billing: new BillingOptions { FlattenFormCredits = credits });

        await controller.FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(ScriptedForm()) });

        Assert.Equal(credits, Assert.Single(controller.Calls).BaseCost);
    }

    [Fact]
    public async Task Trusted_caller_is_not_charged()
    {
        var controller = Controller(billing: new BillingOptions { FlattenFormCredits = 5 }, trusted: true);

        await controller.FlattenForm(new FlattenFormInput { PdfContent = Convert.ToBase64String(ScriptedForm()) });

        Assert.Equal(0, Assert.Single(controller.Calls).BaseCost);
    }

    [Fact]
    public void Default_price_is_one_credit() => Assert.Equal(1, new BillingOptions().FlattenFormCredits);
}
