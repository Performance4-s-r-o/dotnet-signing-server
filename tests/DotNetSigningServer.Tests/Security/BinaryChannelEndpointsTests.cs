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
/// #74: interní binární kanál na dalších endpointech s PDF (timestamp, sign-pfx, extend-signature,
/// inspect-signatures, visual-sign, seal, attachment, convert/pdfa). BEZ Dockeru a databáze:
/// kontrolery se volají přímo, službu pod nimi nahrazuje mock, který vrací otisk bajtů, které
/// dostal, a <c>RunOperationAsync</c> (stav operací v PostgreSQL) zachytává testovací potomek.
/// Co se tu zkouší: že binární akce dá stejný výsledek jako JSON akce, že odmítne totéž, co
/// JSON, a že otisk pro idempotenci závisí na dokumentu.
/// </summary>
public class BinaryChannelEndpointsTests
{
    private const long PdfMaxBytes = 200_000;
    private const int PdfMaxPages = 5;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ── harness ──────────────────────────────────────────────────────────

    private sealed record Call(string Operation, string Fingerprint, int BaseCost, string Body, int Opens = 0);

    /// <summary>Otisk bajtů, které služba dostala: stejný dokument → stejný výsledek, v obou kanálech.</summary>
    private static string Stamp(byte[] pdf) => Convert.ToBase64String(SHA256.HashData(pdf));

    private static string StampBase64(string base64) => Stamp(Convert.FromBase64String(base64));

    private sealed class Spy
    {
        public int JsonCalls;
        public readonly List<byte[]> BinaryInputs = new();
    }

    private sealed class SigningHarness : PdfSigningApiController
    {
        public readonly List<Call> Calls = new();

        public SigningHarness(
            ApplicationDbContext db, PdfSigningService signing, PdfSealingService sealing, PdfLtvService ltv,
            ContentLimitGuard limits, PdfTemplateService templates, IStringLocalizer<SharedStrings> localizer)
            : base(db, signing, sealing, ltv, new Mock<IApiAuthService>().Object, NullLogger<PdfSigningApiController>.Instance,
                limits, TestHelpers.WrapOptions(new BillingOptions()), TestHelpers.CreateMockEnvironment(), templates,
                new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().Object, localizer)
        {
        }

        protected override async Task<IActionResult> RunOperationAsync(
            User user, string operation, string fingerprint, int baseCost,
            Func<CancellationToken, Task<PaidResult>> work, string? presignId = null, Func<Task>? afterCompleted = null)
        {
            // Zapsáno dřív než práce: otisk a cena platí i pro operaci, která skončí chybou.
            var index = Calls.Count;
            Calls.Add(new Call(operation, fingerprint, baseCost, "", PdfOpenCounter.Current));
            var result = await work(CancellationToken.None);
            Calls[index] = Calls[index] with { Body = JsonSerializer.Serialize(result.Body, result.Body.GetType(), WebJson) };
            return Ok(result.Body);
        }
    }

    private sealed class UtilityHarness : PdfUtilityApiController
    {
        public readonly List<Call> Calls = new();

        public UtilityHarness(
            ApplicationDbContext db, ContentLimitGuard limits, PdfTemplateService templates,
            PdfConversionService conversion, IStringLocalizer<SharedStrings> localizer)
            : base(db, new Mock<IApiAuthService>().Object, NullLogger<PdfUtilityApiController>.Instance, limits,
                TestHelpers.WrapOptions(new BillingOptions()), TestHelpers.CreateMockEnvironment(), templates, conversion, localizer)
        {
        }

        protected override async Task<IActionResult> RunOperationAsync(
            User user, string operation, string fingerprint, int baseCost,
            Func<CancellationToken, Task<PaidResult>> work, string? presignId = null, Func<Task>? afterCompleted = null)
        {
            // Zapsáno dřív než práce: otisk a cena platí i pro operaci, která skončí chybou.
            var index = Calls.Count;
            Calls.Add(new Call(operation, fingerprint, baseCost, "", PdfOpenCounter.Current));
            var result = await work(CancellationToken.None);
            Calls[index] = Calls[index] with { Body = JsonSerializer.Serialize(result.Body, result.Body.GetType(), WebJson) };
            return Ok(result.Body);
        }
    }

    /// <summary>Jeden pokus o volání: čerstvé služby, čerstvý účet, čerstvé počítadlo.</summary>
    private sealed class Attempt
    {
        public readonly Spy Spy = new();
        public readonly List<Call> Calls = new();
        private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();
        private readonly User _user;
        private readonly PdfSigningService _signing;
        private readonly PdfSealingService _sealing;
        private readonly PdfConversionService _conversion;
        private readonly ContentLimitGuard _limits;
        private readonly PdfTemplateService _templates;
        private readonly IStringLocalizer<SharedStrings> _localizer;
        private readonly ServiceProvider _services;

        public Attempt()
        {
            var limitOptions = new LimitsOptions
            {
                PdfMaxBytes = PdfMaxBytes,
                PdfMaxPages = PdfMaxPages,
                ImageMaxBytes = 100_000,
                RequestBodyLimitBytes = 1_000_000,
            };
            _limits = new ContentLimitGuard(TestHelpers.WrapOptions(limitOptions));
            _user = TestHelpers.CreateTestUser();
            _user.CreditsRemaining = 100;
            _user.SealAllowed = true;
            _db.Users.Add(_user);
            _db.SaveChanges();

            var localizer = new Mock<IStringLocalizer<SharedStrings>>();
            localizer.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
            localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string k, object[] _) => new LocalizedString(k, k));
            _localizer = localizer.Object;

            var factory = new Mock<IStringLocalizerFactory>();
            _templates = new PdfTemplateService(_db, NullLogger<PdfTemplateService>.Instance, _limits, factory.Object);

            // PdfSigningService: oba přetížení vrací otisk dokumentu, který dostala; počítá, které bylo voláno.
            var signing = new Mock<PdfSigningService>(null, null, null);
            signing.Setup(s => s.SignWithPfx(It.IsAny<PfxSignInput>())).Returns((PfxSignInput i) => Json(i.PdfContent));
            signing.Setup(s => s.SignWithPfx(It.IsAny<PfxSignInput>(), It.IsAny<byte[]>())).Returns((PfxSignInput _, byte[] p) => Bin(p));
            signing.Setup(s => s.ApplyDocumentTimestamp(It.IsAny<DocumentTimestampInput>())).Returns((DocumentTimestampInput i) => Json(i.PdfContent));
            signing.Setup(s => s.ApplyDocumentTimestamp(It.IsAny<DocumentTimestampInput>(), It.IsAny<byte[]>())).Returns((DocumentTimestampInput _, byte[] p) => Bin(p));
            signing.Setup(s => s.ApplyVisualSign(It.IsAny<VisualSignInput>())).Returns((VisualSignInput i) => Json(i.PdfContent));
            signing.Setup(s => s.ApplyVisualSign(It.IsAny<VisualSignInput>(), It.IsAny<byte[]>())).Returns((VisualSignInput _, byte[] p) => Bin(p));
            signing.Setup(s => s.AddAttachment(It.IsAny<AddAttachmentInput>())).Returns((AddAttachmentInput i) => Json(i.PdfContent));
            signing.Setup(s => s.AddAttachment(It.IsAny<AddAttachmentInput>(), It.IsAny<byte[]?>())).Returns((AddAttachmentInput _, byte[]? p) => Bin(p!));
            _signing = signing.Object;

            var sealing = new Mock<PdfSealingService>(TestHelpers.WrapOptions(new SealOptions()), _signing, new PdfVisualSigningService(), null);
            sealing.Setup(s => s.ApplySeal(It.IsAny<SealInput>())).Returns((SealInput i) => Json(i.PdfContent));
            sealing.Setup(s => s.ApplySeal(It.IsAny<SealInput>(), It.IsAny<byte[]>())).Returns((SealInput _, byte[] p) => Bin(p));
            _sealing = sealing.Object;

            var conversion = new Mock<PdfConversionService>(NullLogger<PdfConversionService>.Instance);
            conversion.Setup(s => s.ConvertToPdfA(It.IsAny<ConvertToPdfAInput>())).Returns((ConvertToPdfAInput i) => Json(i.PdfContent));
            conversion.Setup(s => s.ConvertToPdfA(It.IsAny<ConvertToPdfAInput>(), It.IsAny<byte[]>())).Returns((ConvertToPdfAInput _, byte[] p) => Bin(p));
            _conversion = conversion.Object;

            _services = new ServiceCollection()
                .AddSingleton(new PdfSafetyGuard(TestHelpers.WrapOptions(limitOptions)))
                .AddSingleton(TestHelpers.WrapOptions(limitOptions))
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()))
                .BuildServiceProvider();
        }

        private string Json(string base64)
        {
            Spy.JsonCalls++;
            return StampBase64(base64);
        }

        private string Bin(byte[] pdf)
        {
            Spy.BinaryInputs.Add(pdf);
            return Stamp(pdf);
        }

        private ControllerContext Context(HttpContent? multipart, byte[]? body)
        {
            var http = new DefaultHttpContext { RequestServices = _services };
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString()) }, ApiKeyAuthenticationHandler.SchemeName));
            if (multipart != null)
            {
                body = multipart.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                http.Request.ContentType = multipart.Headers.ContentType!.ToString();
            }
            if (body != null)
            {
                http.Request.Body = new MemoryStream(body);
                http.Request.ContentLength = body.Length;
            }
            return new ControllerContext { HttpContext = http };
        }

        public SigningHarness Signing(HttpContent? multipart = null)
        {
            var c = new SigningHarness(_db, _signing, _sealing, new PdfLtvService(), _limits, _templates, _localizer)
            {
                ControllerContext = Context(multipart, null),
            };
            return Track(c, c.Calls);
        }

        public UtilityHarness Utility(HttpContent? multipart = null)
        {
            var c = new UtilityHarness(_db, _limits, _templates, _conversion, _localizer)
            {
                ControllerContext = Context(multipart, null),
            };
            return Track(c, c.Calls);
        }

        private T Track<T>(T controller, List<Call> calls)
        {
            _calls = calls;
            return controller;
        }

        private List<Call> _calls = new();
        public IReadOnlyList<Call> RecordedCalls => _calls;

        /// <summary>Účet bez oprávnění k pečeti.</summary>
        public void RevokeSeal()
        {
            _user.SealAllowed = false;
            _db.SaveChanges();
        }
    }

    // ── endpointy ────────────────────────────────────────────────────────

    /// <summary>Endpoint: metadata (bez pdfContent) a dvě akce, které je obsluhují.</summary>
    private sealed record Endpoint(
        string Name,
        string Operation,
        object Metadata,
        Func<Attempt, HttpContent?, Task<IActionResult>> Binary,
        Func<Attempt, string, Task<IActionResult>> Json)
    {
        public override string ToString() => Name;
    }

    private static T Body<T>(object metadata, string pdfBase64)
    {
        var node = JsonSerializer.SerializeToNode(metadata, metadata.GetType(), WebJson)!.AsObject();
        node["pdfContent"] = pdfBase64;
        return node.Deserialize<T>(WebJson)!;
    }

    private static readonly SignRect Rect = new() { X = 10, Y = 10, Width = 200, Height = 50 };

    private static readonly Endpoint[] All =
    {
        new("sign-pfx", "sign-pfx",
            new { pfxContent = TestHelpers.CreateTestCertificate().PfxBase64, pfxPassword = "test-password", signRect = Rect },
            (a, m) => a.Signing(m).SignWithPfxBinary(),
            (a, pdf) => a.Signing().SignWithPfx(Body<PfxSignInput>(
                new { pfxContent = TestHelpers.CreateTestCertificate().PfxBase64, pfxPassword = "test-password", signRect = Rect }, pdf))),
        new("timestamp", "timestamp", new { signRect = Rect },
            (a, m) => a.Signing(m).ApplyTimestampBinary(),
            (a, pdf) => a.Signing().ApplyTimestamp(Body<DocumentTimestampInput>(new { signRect = Rect }, pdf))),
        new("visual-sign", "visual-sign", new { signRect = Rect },
            (a, m) => a.Signing(m).ApplyVisualSignBinary(),
            (a, pdf) => a.Signing().ApplyVisualSign(Body<VisualSignInput>(new { signRect = Rect }, pdf))),
        new("seal", "seal", new { signRect = Rect },
            (a, m) => a.Signing(m).ApplySealBinary(),
            (a, pdf) => a.Signing().ApplySeal(Body<SealInput>(new { signRect = Rect }, pdf))),
        new("attachment", "attachment",
            new { attachmentContent = Convert.ToBase64String("příloha"u8.ToArray()), fileName = "a.txt" },
            (a, m) => a.Signing(m).AddAttachmentBinary(),
            (a, pdf) => a.Signing().AddAttachment(Body<AddAttachmentInput>(
                new { attachmentContent = Convert.ToBase64String("příloha"u8.ToArray()), fileName = "a.txt" }, pdf))),
        new("extend-signature", "extend-signature", new { addArchiveTimestamp = false },
            (a, m) => a.Signing(m).ExtendSignatureBinary(),
            (a, pdf) => a.Signing().ExtendSignature(Body<ExtendSignatureInput>(new { addArchiveTimestamp = false }, pdf))),
        new("inspect-signatures", "", new { },
            (a, m) => a.Signing(m).InspectSignaturesBinary(),
            (a, pdf) => a.Signing().InspectSignatures(Body<InspectSignaturesInput>(new { }, pdf))),
        new("convert/pdfa", "pdfa", new { conformance = "PDF/A-2B" },
            (a, m) => a.Utility(m).ConvertToPdfABinary(),
            (a, pdf) => a.Utility().ConvertToPdfA(Body<ConvertToPdfAInput>(new { conformance = "PDF/A-2B" }, pdf))),
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var e in All) data.Add(e.Name);
        return data;
    }

    private static Endpoint Find(string name) => All.Single(e => e.Name == name);

    // ── dokumenty a těla ─────────────────────────────────────────────────

    private static byte[] Pdf(int pages = 1, Action<PdfDocument>? extra = null, int noiseBytes = 0, int seed = 0)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms, new WriterProperties().SetCompressionLevel(0))))
        {
            for (var i = 0; i < pages; i++) doc.AddNewPage(PageSize.A4);
            if (noiseBytes > 0)
            {
                var data = new byte[noiseBytes];
                new Random(7).NextBytes(data);
                doc.GetFirstPage().GetPdfObject().Put(new PdfName("Noise"), new PdfStream(data));
            }
            // Dva různé dokumenty se stejným počtem stran se liší jen tady.
            doc.GetDocumentInfo().SetTitle("doc-" + seed);
            extra?.Invoke(doc);
        }
        return ms.ToArray();
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
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(part, BinaryPdfChannel.PdfPart, "document.pdf");
        }
        extra?.Invoke(content);
        return content;
    }

    private static (int Status, string? Code, string Body) Describe(IActionResult result)
    {
        var status = (result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? 200;
        var value = (result as ObjectResult)?.Value;
        var body = value == null ? "" : JsonSerializer.Serialize(value, value.GetType(), WebJson);
        string? code = null;
        if (value != null)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
        }
        return (status, code, body);
    }

    private static Task<IActionResult> RunBinary(Endpoint e, Attempt a, byte[] pdf) =>
        e.Binary(a, Multipart(e.Metadata, pdf));

    private static Task<IActionResult> RunJson(Endpoint e, Attempt a, byte[] pdf) =>
        e.Json(a, Convert.ToBase64String(pdf));

    // ── PDF se v jednom požadavku čte nejvýš jednou před prací (#70) ────

    [Fact]
    public async Task Pdfa_opens_the_pdf_once_before_the_work_in_both_channels()
    {
        var e = Find("convert/pdfa");
        var pdf = Pdf(pages: 3);

        PdfOpenCounter.Begin();
        var json = new Attempt();
        await RunJson(e, json, pdf);
        Assert.Equal(1, json.RecordedCalls.Single().Opens);
        Assert.Equal(1, PdfOpenCounter.Current); // the mocked conversion opens nothing

        PdfOpenCounter.Begin();
        var binary = new Attempt();
        await RunBinary(e, binary, pdf);
        Assert.Equal(1, binary.RecordedCalls.Single().Opens);
    }

    [Fact]
    public async Task Fill_with_a_direct_pdf_opens_it_once_before_the_work()
    {
        var pdf = Pdf(pages: 2);
        var input = new FillPdfInput
        {
            PdfContent = Convert.ToBase64String(pdf),
            Fields = new List<PdfFieldDefinition>
            {
                new() { FieldName = "F", Type = PdfFieldType.Text, Rect = new SignRect { X = 10, Y = 10, Width = 100, Height = 20 }, Page = 1, FontSize = 12 },
            },
            Data = new List<FillDataSet> { new() },
        };

        PdfOpenCounter.Begin();
        var a = new Attempt();
        try { await a.Utility().FillPdf(input); }
        catch (InvalidOperationException) { /* the test harness has no ProblemDetails; only the count before the work matters */ }

        Assert.Equal(1, a.RecordedCalls.Single().Opens);
    }

    // ── binární varianta dá totéž co JSON ────────────────────────────────

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Binary_returns_the_same_result_as_json(string name)
    {
        var e = Find(name);
        var pdf = Pdf();

        var json = new Attempt();
        var jsonResult = Describe(await RunJson(e, json, pdf));
        var binary = new Attempt();
        var binaryResult = Describe(await RunBinary(e, binary, pdf));

        // extend-signature nad dokumentem bez podpisu odpoví 400 v obou kanálech (stejně); jinak 200.
        if (name != "extend-signature") Assert.Equal(200, jsonResult.Status);
        Assert.Equal(jsonResult, binaryResult);
        // Služba dostala přesně bajty z části pdf; JSON přetížení se v binární větvi nevolalo.
        if (e.Operation != "extend-signature" && e.Name != "inspect-signatures")
        {
            Assert.Equal(0, binary.Spy.JsonCalls);
            Assert.Equal(pdf, Assert.Single(binary.Spy.BinaryInputs));
            Assert.Equal(1, json.Spy.JsonCalls);
            Assert.Empty(json.Spy.BinaryInputs);
        }
        if (e.Operation != "")
        {
            var j = Assert.Single(json.RecordedCalls);
            Assert.Equal(e.Operation, j.Operation);
            var b = Assert.Single(binary.RecordedCalls);
            Assert.Equal(j.Operation, b.Operation);
            Assert.Equal(j.BaseCost, b.BaseCost);
            Assert.Equal(j.Body, b.Body);
        }
    }

    // ── odmítnuté dokumenty: stejně jako JSON ────────────────────────────

    public static TheoryData<string, string> RefusedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in All)
        {
            foreach (var code in new[] { "PDF_INVALID", "PDF_SIZE_EXCEEDED", "PDF_TOO_MANY_PAGES", "PDF_ACTIVE_CONTENT" })
            {
                data.Add(e.Name, code);
            }
        }
        return data;
    }

    private static byte[] RefusedPdf(string code) => code switch
    {
        "PDF_INVALID" => "this is not a pdf"u8.ToArray(),
        "PDF_SIZE_EXCEEDED" => Pdf(noiseBytes: (int)PdfMaxBytes + 10_000),
        "PDF_TOO_MANY_PAGES" => Pdf(pages: PdfMaxPages + 1),
        "PDF_ACTIVE_CONTENT" => Pdf(extra: doc => doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript("app.alert('hi')"))),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public async Task Binary_refuses_what_json_refuses(string name, string code)
    {
        var e = Find(name);
        var pdf = RefusedPdf(code);

        var json = new Attempt();
        var jsonResult = Describe(await RunJson(e, json, pdf));
        var binary = new Attempt();
        var binaryResult = Describe(await RunBinary(e, binary, pdf));

        Assert.Equal(jsonResult.Status, binaryResult.Status);
        Assert.Equal(jsonResult.Code, binaryResult.Code);
        // Nic se neprovedlo ani neúčtovalo.
        Assert.Empty(binary.RecordedCalls);
        Assert.Empty(binary.Spy.BinaryInputs);
        // inspect-signatures nehlídá aktivní obsah (kontroluje dokumenty, které nikdo neotevírá) — ani JSON.
        if (name == "inspect-signatures" && code == "PDF_ACTIVE_CONTENT")
        {
            Assert.Equal(200, binaryResult.Status);
        }
        else
        {
            Assert.Equal(400, binaryResult.Status);
            Assert.Equal(code, binaryResult.Code);
        }
    }

    // ── tvar těla ────────────────────────────────────────────────────────

    public static TheoryData<string, string> InvalidBodies()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in All)
        {
            foreach (var variant in new[] { "missing-pdf", "missing-metadata", "extra-part", "pdf-twice", "inline-pdfcontent", "metadata-not-json", "not-multipart" })
            {
                data.Add(e.Name, variant);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Malformed_binary_body_is_MULTIPART_INVALID(string name, string variant)
    {
        var e = Find(name);
        var pdf = Pdf();
        HttpContent body = variant switch
        {
            "missing-pdf" => Multipart(e.Metadata, null),
            "missing-metadata" => Multipart(null, pdf),
            "extra-part" => Multipart(e.Metadata, pdf, c => c.Add(new StringContent("x"), "other")),
            "pdf-twice" => Multipart(e.Metadata, pdf, c =>
            {
                var again = new ByteArrayContent(pdf);
                again.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                c.Add(again, BinaryPdfChannel.PdfPart, "again.pdf");
            }),
            "inline-pdfcontent" => Multipart(
                JsonSerializer.Serialize(e.Metadata, WebJson).TrimEnd('}') + (e.Metadata.GetType().GetProperties().Length == 0 ? "" : ",") + "\"pdfContent\":\"AAAA\"}",
                pdf),
            "metadata-not-json" => Multipart("this is { not json", pdf),
            "not-multipart" => new StringContent("{}", Encoding.UTF8, "application/json"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var attempt = new Attempt();
        var result = Describe(await e.Binary(attempt, body));

        Assert.Equal(400, result.Status);
        Assert.Equal(BinaryPdfChannel.InvalidCode, result.Code);
        Assert.Empty(attempt.RecordedCalls);
        Assert.Empty(attempt.Spy.BinaryInputs);
    }

    // ── otisk pro idempotenci ────────────────────────────────────────────

    public static TheoryData<string> FingerprintNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in All.Where(x => x.Operation != "")) data.Add(e.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(FingerprintNames))]
    public async Task Same_operation_twice_has_the_same_fingerprint_and_another_document_another(string name)
    {
        var e = Find(name);
        var doc = Pdf(seed: 1);
        var other = Pdf(seed: 2);
        Assert.NotEqual(doc, other);

        var first = new Attempt();
        await RunBinary(e, first, doc);
        var again = new Attempt();
        await RunBinary(e, again, doc);
        var different = new Attempt();
        await RunBinary(e, different, other);

        var f1 = Assert.Single(first.RecordedCalls).Fingerprint;
        Assert.Equal(f1, Assert.Single(again.RecordedCalls).Fingerprint);
        Assert.NotEqual(f1, Assert.Single(different.RecordedCalls).Fingerprint);
    }

    [Fact]
    public void Binary_fingerprint_never_depends_on_the_empty_PdfContent_alone()
    {
        // Metadata se liší jen dokumentem; PdfContent je v obou prázdný.
        var input = new SealInput();
        var a = ApiControllerBase.BinaryFingerprint("seal", input, new byte[] { 1, 2, 3 });
        var b = ApiControllerBase.BinaryFingerprint("seal", input, new byte[] { 1, 2, 4 });
        Assert.NotEqual(a, b);
        Assert.Equal(a, ApiControllerBase.BinaryFingerprint("seal", input, new byte[] { 1, 2, 3 }));
        // Jiná operace nad týmž dokumentem je jiná operace.
        Assert.NotEqual(a, ApiControllerBase.BinaryFingerprint("timestamp", input, new byte[] { 1, 2, 3 }));
        // Jiná metadata nad týmž dokumentem také.
        Assert.NotEqual(a, ApiControllerBase.BinaryFingerprint("seal", new SealInput { Reason = "x" }, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task Seal_binary_checks_the_grant_before_reading_the_body()
    {
        var attempt = new Attempt();
        var controller = attempt.Signing(Multipart(All.Single(e => e.Name == "seal").Metadata, Pdf()));
        // Účet bez oprávnění: 403 a tělo zůstalo nepřečtené.
        var body = controller.Request.Body;
        attempt.RevokeSeal();

        var result = Describe(await controller.ApplySealBinary());

        Assert.Equal(403, result.Status);
        Assert.Equal("SEAL_NOT_PERMITTED", result.Code);
        Assert.Equal(0, body.Position);
    }

    [Theory]
    [InlineData("Reason", "Podpis\nSouhlasím", "SEAL_REASON_INVALID")]
    [InlineData("Location", "Praha\u202Egpj", "SEAL_LOCATION_INVALID")]
    [InlineData("Reason", "bez řádku\u200B", "SEAL_REASON_INVALID")]
    [InlineData("Location", "Praha\uFE0F", "SEAL_LOCATION_INVALID")]
    public async Task Seal_refuses_reason_and_location_that_do_not_belong_in_a_signature_dictionary(string field, string value, string code)
    {
        var meta = new Dictionary<string, object> { ["signRect"] = Rect, [field.ToLowerInvariant()] = value };

        var binary = new Attempt();
        var binaryResult = Describe(await binary.Signing(Multipart(meta, Pdf())).ApplySealBinary());
        Assert.Equal(400, binaryResult.Status);
        Assert.Equal(code, binaryResult.Code);
        Assert.Empty(binary.Spy.BinaryInputs);

        var json = new Attempt();
        var jsonResult = Describe(await json.Signing().ApplySeal(Body<SealInput>(meta, Convert.ToBase64String(Pdf()))));
        Assert.Equal(400, jsonResult.Status);
        Assert.Equal(code, jsonResult.Code);
        Assert.Equal(0, json.Spy.JsonCalls);
    }
}
