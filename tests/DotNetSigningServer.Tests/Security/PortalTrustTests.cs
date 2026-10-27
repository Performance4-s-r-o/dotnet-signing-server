using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #78: the portal's token only on the internal listener, its tenant headers and the billing
/// bypass only from there. #106: CF-Connecting-IP only from a configured proxy.
/// </summary>
[Collection("ApiGate")]
public class PortalTrustTests : IDisposable
{
    private const int PublicPort = 8085;
    private const int InternalPort = 8086;

    private static readonly Guid PortalUserId = Guid.NewGuid();
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();

    public void Dispose() => _db.Dispose();

    private static PortalTrustOptions Trust(int? internalPort = InternalPort) =>
        new() { InternalPort = internalPort, TrustedCallerUserIds = new HashSet<Guid> { PortalUserId } };

    private static User UserWith(Guid id) => new() { Id = id, Email = $"{id:N}@example.test", IsActive = true, CreditsRemaining = 100 };

    // ── Authentication handler: which port the portal's token works on ──────────────────────────

    private static async Task<(AuthenticateResult Result, DefaultHttpContext Context)> AuthenticateAsync(
        PortalTrustOptions trust, Guid userId, int localPort, bool browserToken = false)
    {
        var apiAuth = new Mock<IApiAuthService>();
        apiAuth.Setup(a => a.ValidateTokenDetailedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IPAddress?>()))
            .ReturnsAsync(new ApiTokenValidation(UserWith(userId), Guid.NewGuid(), browserToken));
        var monitor = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        monitor.Setup(m => m.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthenticationHandler(monitor.Object, NullLoggerFactory.Instance, UrlEncoder.Default, apiAuth.Object, trust);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/presign";
        context.Request.Headers.Authorization = "Bearer some-token";
        context.Connection.LocalPort = localPort;
        await handler.InitializeAsync(new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)), context);
        return (await handler.AuthenticateAsync(), context);
    }

    [Fact]
    public async Task Portal_token_on_the_public_port_is_not_authenticated()
    {
        var (result, _) = await AuthenticateAsync(Trust(), PortalUserId, PublicPort);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Portal_token_on_the_internal_port_is_the_trusted_caller()
    {
        var (result, _) = await AuthenticateAsync(Trust(), PortalUserId, InternalPort);
        Assert.True(result.Succeeded);
        Assert.Equal("true", result.Principal!.FindFirst(PortalTrust.TrustedCallerClaim)?.Value);
    }

    [Fact]
    public async Task A_customer_token_works_on_the_public_port_and_is_not_trusted()
    {
        var (result, _) = await AuthenticateAsync(Trust(), Guid.NewGuid(), PublicPort);
        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.FindFirst(PortalTrust.TrustedCallerClaim));
    }

    [Fact]
    public async Task A_browser_token_of_the_portal_account_is_not_bound_to_the_internal_port()
    {
        var (result, _) = await AuthenticateAsync(Trust(), PortalUserId, PublicPort, browserToken: true);
        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.FindFirst(PortalTrust.TrustedCallerClaim));
    }

    [Fact]
    public async Task Without_an_internal_port_nothing_changes_for_the_portal_token()
    {
        var (result, _) = await AuthenticateAsync(Trust(internalPort: null), PortalUserId, PublicPort);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Portal_token_from_the_public_hostname_gets_401()
    {
        var (result, context) = await AuthenticateAsync(Trust(), PortalUserId, PublicPort);
        if (result.Succeeded) context.User = result.Principal!;

        var services = new ServiceCollection();
        services.AddSingleton<IStringLocalizerFactory>(new KeyEchoLocalizerFactory());
        services.AddScoped(_ => _db);
        context.RequestServices = services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var reached = false;
        var gate = new ApiGateMiddleware(_ => { reached = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions()));

        await gate.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(reached);
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Internal_port_without_the_portal_account_fails_startup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PortalTrustOptions.From(Config(("INTERNAL_API_PORT", "8086"), ("TRUSTED_CALLER_USER_IDS", ""))));
    }

    [Fact]
    public void Unset_and_placeholder_values_mean_no_internal_listener()
    {
        var options = PortalTrustOptions.From(Config(
            ("INTERNAL_API_PORT", null), ("TRUSTED_CALLER_USER_IDS", "{{project.DOTNET_TRUSTED_CALLER_USER_IDS}}")));
        Assert.Null(options.InternalPort);
        Assert.Empty(options.TrustedCallerUserIds);
    }

    [Fact]
    public void Configured_portal_accounts_are_parsed()
    {
        var other = Guid.NewGuid();
        var options = PortalTrustOptions.From(Config(("INTERNAL_API_PORT", "8086"), ("TRUSTED_CALLER_USER_IDS", $"{PortalUserId}, {other}")));
        Assert.Equal(8086, options.InternalPort);
        Assert.Equal(new HashSet<Guid> { PortalUserId, other }, options.TrustedCallerUserIds);
    }

    [Fact]
    public void Empty_port_placeholder_needs_no_account_id()
    {
        var options = PortalTrustOptions.From(Config(
            ("INTERNAL_API_PORT", "{{project.DOTNET_INTERNAL_API_PORT}}"), ("TRUSTED_CALLER_USER_IDS", null)));
        Assert.Null(options.InternalPort);
        Assert.Empty(options.TrustedCallerUserIds);
    }

    [Fact]
    public void Ids_without_port_are_flagged_as_trusted_on_every_port()
    {
        var options = PortalTrustOptions.From(Config(("TRUSTED_CALLER_USER_IDS", PortalUserId.ToString())));
        Assert.True(options.TrustedOnEveryPort);
        Assert.False(PortalTrustOptions.From(Config(("INTERNAL_API_PORT", "8086"), ("TRUSTED_CALLER_USER_IDS", PortalUserId.ToString()))).TrustedOnEveryPort);
    }

    [Fact]
    public void Legacy_name_is_still_read_and_flagged()
    {
        var options = PortalTrustOptions.From(Config(("INTERNAL_API_PORT", "8086"), ("PORTAL_USER_IDS", PortalUserId.ToString())));
        Assert.Equal(new HashSet<Guid> { PortalUserId }, options.TrustedCallerUserIds);
        Assert.True(options.UsesLegacyVariable);
    }

    [Fact]
    public void New_name_alone_is_not_flagged_as_legacy()
    {
        var options = PortalTrustOptions.From(Config(("INTERNAL_API_PORT", "8086"), ("TRUSTED_CALLER_USER_IDS", PortalUserId.ToString())));
        Assert.False(options.UsesLegacyVariable);
    }

    [Fact]
    public void Both_names_with_the_same_ids_are_accepted()
    {
        var id = PortalUserId.ToString();
        var options = PortalTrustOptions.From(Config(
            ("INTERNAL_API_PORT", "8086"), ("TRUSTED_CALLER_USER_IDS", id), ("PORTAL_USER_IDS", id)));
        Assert.Equal(new HashSet<Guid> { PortalUserId }, options.TrustedCallerUserIds);
    }

    [Fact]
    public void Both_names_with_different_ids_fail_startup()
    {
        Assert.Throws<InvalidOperationException>(() => PortalTrustOptions.From(Config(
            ("INTERNAL_API_PORT", "8086"),
            ("TRUSTED_CALLER_USER_IDS", PortalUserId.ToString()),
            ("PORTAL_USER_IDS", Guid.NewGuid().ToString()))));
    }

    [Theory]
    [InlineData("http://+:8085", "http://+:8085;http://+:8086")]
    [InlineData("http://+:8085;http://+:8086", "http://+:8085;http://+:8086")]
    [InlineData(null, "http://localhost:5000;http://+:8086")]
    public void The_internal_listener_is_added_next_to_the_public_one(string? urls, string expected)
    {
        Assert.Equal(expected, PortalTrustOptions.WithInternalListener(urls, InternalPort));
    }

    // ── Tenant / request id headers ─────────────────────────────────────────────────────────────

    private static DefaultHttpContext CallerContext(bool trusted, string? tenant = "tenant-a", string? requestId = "req-1")
    {
        var context = new DefaultHttpContext();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, PortalUserId.ToString()) };
        if (trusted) claims.Add(new Claim(PortalTrust.TrustedCallerClaim, "true"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
        if (tenant != null) context.Request.Headers[PortalTrust.TenantHeader] = tenant;
        if (requestId != null) context.Request.Headers[PortalTrust.RequestIdHeader] = requestId;
        return context;
    }

    [Fact]
    public void Tenant_headers_of_the_trusted_caller_are_read()
    {
        Assert.Equal(new PortalCaller("tenant-a", "req-1"), PortalTrust.CallerOf(CallerContext(trusted: true)));
    }

    [Fact]
    public void Tenant_headers_of_any_other_caller_are_ignored()
    {
        Assert.Equal(PortalCaller.None, PortalTrust.CallerOf(CallerContext(trusted: false)));
    }

    [Fact]
    public void Tenant_header_outside_the_safe_alphabet_is_dropped()
    {
        var caller = PortalTrust.CallerOf(CallerContext(trusted: true, tenant: "a\nb", requestId: new string('x', 200)));
        Assert.Null(caller.TenantId);
        Assert.Null(caller.RequestId);
    }

    [Theory]
    [InlineData(true, "tenant-a", "req-1")]
    [InlineData(false, null, null)]
    public async Task A_failed_call_is_recorded_with_the_portal_tenant_and_request_id(bool trusted, string? tenant, string? requestId)
    {
        var context = CallerContext(trusted);
        context.Request.Path = "/api/presign";
        context.Items[ApiRequestLoggingMiddleware.AuthenticatedUserIdItemKey] = PortalUserId;
        context.RequestServices = new ServiceCollection().AddSingleton(_db).BuildServiceProvider();
        var middleware = new ApiRequestLoggingMiddleware(c => { c.Response.StatusCode = 400; return Task.CompletedTask; },
            NullLogger<ApiRequestLoggingMiddleware>.Instance);

        await middleware.Invoke(context);

        var record = await _db.UsageRecords.SingleAsync();
        Assert.Equal(tenant, record.TenantId);
        Assert.Equal(requestId, record.RequestId);
    }

    [Fact]
    public void Loki_lines_carry_the_portal_scope()
    {
        var scopes = new LoggerExternalScopeProvider();
        using (scopes.Push(new Dictionary<string, object?>
        {
            [PortalCallerScopeMiddleware.TenantScopeKey] = "tenant-a",
            [PortalCallerScopeMiddleware.RequestIdScopeKey] = "req-1",
        }))
        {
            Assert.Equal(" [tenant=tenant-a requestId=req-1]", DotNetSigningServer.Logging.LokiLogger.PortalScopeSuffix(scopes));
        }
        Assert.Null(DotNetSigningServer.Logging.LokiLogger.PortalScopeSuffix(scopes));
    }

    // ── Presign / sign: tenant stored, tenant checked ───────────────────────────────────────────

    private PdfSigningApiController Controller(HttpContext context, LimitsOptions? limits = null)
    {
        limits ??= new LimitsOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore(); // Problem() of the unchecked path needs its factory
        services.AddSingleton(_db);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(limits));
        services.AddSingleton(new PdfSafetyGuard(Microsoft.Extensions.Options.Options.Create(limits)));
        services.AddSingleton(new ApiOperationService(_db, new EphemeralDataProtectionProvider(),
            NullLogger<ApiOperationService>.Instance, Microsoft.Extensions.Options.Options.Create(new OperationsOptions())));
        context.RequestServices = services.BuildServiceProvider();

        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name));
        return new PdfSigningApiController(
            _db, new PdfSigningService(), null!, null!, Mock.Of<IApiAuthService>(),
            NullLogger<PdfSigningApiController>.Instance,
            new ContentLimitGuard(Microsoft.Extensions.Options.Options.Create(new LimitsOptions())),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions()),
            TestHelpers.CreateMockEnvironment("Testing"), null!, new EphemeralDataProtectionProvider(), localizer.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private async Task<User> PortalUserInDbAsync()
    {
        var user = UserWith(PortalUserId);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Presign_stores_the_tenant_and_request_id_of_the_portal()
    {
        await PortalUserInDbAsync();
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var controller = Controller(CallerContext(trusted: true));

        var result = await controller.PreSign(new PreSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            CertificatePem = certPem,
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        });

        Assert.IsType<OkObjectResult>(result);
        var row = await _db.SigningData.SingleAsync();
        try
        {
            Assert.Equal("tenant-a", row.TenantId);
            Assert.Equal("req-1", row.RequestId);
        }
        finally
        {
            File.Delete(row.PresignedPdfPath);
        }
    }

    private async Task<IActionResult> PresignOverTheCapAsync(bool trusted)
    {
        await PortalUserInDbAsync();
        var path = Path.GetTempFileName();
        _db.SigningData.Add(new SigningData { UserId = PortalUserId, PresignedPdfPath = path });
        await _db.SaveChangesAsync();
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var controller = Controller(CallerContext(trusted), new LimitsOptions { MaxPendingPresignsPerUser = 1 });
        try
        {
            return await controller.PreSign(new PreSignInput
            {
                PdfContent = TestHelpers.CreateMinimalPdfBase64(),
                CertificatePem = certPem,
                SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            });
        }
        finally
        {
            File.Delete(path);
            foreach (var row in _db.SigningData.ToList()) File.Delete(row.PresignedPdfPath);
        }
    }

    [Fact]
    public async Task The_pending_presign_cap_does_not_apply_to_the_portal()
    {
        Assert.IsType<OkObjectResult>(await PresignOverTheCapAsync(trusted: true));
    }

    [Fact]
    public async Task The_pending_presign_cap_applies_to_any_other_caller()
    {
        var result = await PresignOverTheCapAsync(trusted: false);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("TOO_MANY_PENDING_PRESIGNS", CodeOf(result));
    }

    private async Task<IActionResult> CreateTemplateOverTheCapAsync(bool trusted)
    {
        await PortalUserInDbAsync();
        var limits = new LimitsOptions { MaxTemplatesPerUser = 1 };
        var guard = new ContentLimitGuard(Microsoft.Extensions.Options.Options.Create(limits));
        var strings = new Mock<IStringLocalizer>();
        strings.Setup(l => l[It.IsAny<string>()]).Returns((string n) => new LocalizedString(n, n));
        var factory = new Mock<IStringLocalizerFactory>();
        factory.Setup(f => f.Create(It.IsAny<Type>())).Returns(strings.Object);
        var service = new PdfTemplateService(_db, NullLogger<PdfTemplateService>.Instance, guard, factory.Object);
        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string n) => new LocalizedString(n, n));

        var services = new ServiceCollection();
        services.AddSingleton(new PdfSafetyGuard(Microsoft.Extensions.Options.Options.Create(limits)));
        var context = CallerContext(trusted);
        context.RequestServices = services.BuildServiceProvider();
        var controller = new PdfTemplateApiController(
            _db, Mock.Of<IApiAuthService>(), NullLogger<PdfTemplateApiController>.Instance, guard,
            Microsoft.Extensions.Options.Options.Create(new BillingOptions()), TestHelpers.CreateMockEnvironment("Testing"),
            service, null!, localizer.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        CreateTemplateInput Input() => new()
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition>
            {
                new() { FieldName = "F", Type = PdfFieldType.Text, Rect = new SignRect { X = 10, Y = 10, Width = 100, Height = 20 }, Page = 1, FontSize = 12 },
            },
        };
        await service.CreateTemplateAsync(Input(), PortalUserId, default, trustedCaller: true); // one already stored
        return await controller.CreatePdfTemplate(Input());
    }

    [Fact]
    public async Task The_template_cap_does_not_apply_to_the_portal()
    {
        Assert.IsType<OkObjectResult>(await CreateTemplateOverTheCapAsync(trusted: true));
    }

    [Fact]
    public async Task The_template_cap_applies_to_any_other_caller()
    {
        var result = await CreateTemplateOverTheCapAsync(trusted: false);
        Assert.Equal("TOO_MANY_TEMPLATES", CodeOf(result));
    }

    private async Task<string> PresignForAsync(string? tenant)
    {
        await PortalUserInDbAsync();
        var row = new SigningData { UserId = PortalUserId, TenantId = tenant, PresignedPdfPath = "/nonexistent" };
        _db.SigningData.Add(row);
        await _db.SaveChangesAsync();
        return row.Id;
    }

    private static string? CodeOf(IActionResult result) =>
        result is ObjectResult { Value: { } value } ? value.GetType().GetProperty("code")?.GetValue(value) as string : null;

    [Fact]
    public async Task Sign_for_another_tenant_than_the_presign_is_refused()
    {
        var id = await PresignForAsync("tenant-a");
        var result = await Controller(CallerContext(trusted: true, tenant: "tenant-b")).Sign(new SignInput { Id = id });

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(result));
    }

    [Fact]
    public async Task Tenant_header_from_an_untrusted_caller_does_not_unlock_a_tenant_presign()
    {
        var id = await PresignForAsync("tenant-a");
        var result = await Controller(CallerContext(trusted: false, tenant: "tenant-a")).Sign(new SignInput { Id = id });
        Assert.Equal("TENANT_MISMATCH", CodeOf(result));
    }

    [Fact]
    public async Task An_older_presign_without_a_tenant_is_not_checked()
    {
        var id = await PresignForAsync(null);
        var result = await Controller(CallerContext(trusted: true, tenant: "tenant-b")).Sign(new SignInput { Id = id });
        Assert.NotEqual("TENANT_MISMATCH", CodeOf(result));
    }

    [Fact]
    public async Task The_same_tenant_passes_the_check()
    {
        var id = await PresignForAsync("tenant-a");
        var result = await Controller(CallerContext(trusted: true, tenant: "tenant-a")).Sign(new SignInput { Id = id });
        Assert.NotEqual("TENANT_MISMATCH", CodeOf(result));
    }

    private async Task<ApiOperation> CompletedSignForAsync(string tenant)
    {
        await PortalUserInDbAsync();
        // A completed signature: the presign is gone, the operation remembers the tenant.
        var op = new ApiOperation
        {
            UserId = PortalUserId,
            OperationId = "op-1",
            ClientKey = true,
            Operation = "sign",
            PresignId = Guid.NewGuid().ToString(),
            State = ApiOperationState.Completed,
            TenantId = tenant,
        };
        _db.ApiOperations.Add(op);
        await _db.SaveChangesAsync();
        return op;
    }

    [Fact]
    public async Task Replay_of_a_completed_sign_for_another_tenant_is_refused()
    {
        var op = await CompletedSignForAsync("tenant-a");
        var result = await Controller(CallerContext(trusted: true, tenant: "tenant-b")).Sign(new SignInput { Id = op.PresignId! });
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(result));
    }

    [Fact]
    public async Task Replay_of_a_completed_sign_for_the_same_tenant_passes_the_check()
    {
        var op = await CompletedSignForAsync("tenant-a");
        var result = await Controller(CallerContext(trusted: true, tenant: "tenant-a")).Sign(new SignInput { Id = op.PresignId! });
        Assert.NotEqual("TENANT_MISMATCH", CodeOf(result));
    }

    private ApiOperationsController OperationsController(HttpContext context)
    {
        Controller(context); // the same request services
        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name));
        return new ApiOperationsController(
            _db, Mock.Of<IApiAuthService>(), NullLogger<ApiOperationsController>.Instance,
            new ContentLimitGuard(Microsoft.Extensions.Options.Options.Create(new LimitsOptions())),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions()),
            TestHelpers.CreateMockEnvironment("Testing"), null!, localizer.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    [Theory]
    [InlineData("tenant-b", StatusCodes.Status403Forbidden)]
    [InlineData("tenant-a", StatusCodes.Status200OK)]
    public async Task Operation_state_of_another_tenant_is_refused(string callerTenant, int expected)
    {
        await CompletedSignForAsync("tenant-a");
        var result = await OperationsController(CallerContext(trusted: true, tenant: callerTenant)).GetOperation("op-1");
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    // ── Billing bypass ──────────────────────────────────────────────────────────────────────────

    private const string BypassKey = "bypass-key-for-tests";

    private static HttpContext BypassContext(bool trusted, string header, string key)
    {
        var context = CallerContext(trusted);
        context.Request.Headers[header] = key;
        return context;
    }

    [Theory]
    [InlineData(PdfSigningApiController.AttachmentDebitBypassHeader)]
    [InlineData(PdfSigningApiController.LegacyAttachmentDebitBypassHeader)]
    public void Bypass_is_honoured_for_the_portal_under_both_header_names(string header)
    {
        Assert.True(PdfSigningApiController.IsAttachmentDebitBypassAuthorized(BypassContext(true, header, BypassKey), BypassKey));
    }

    [Fact]
    public void Bypass_with_the_right_key_from_another_token_is_refused()
    {
        Assert.False(PdfSigningApiController.IsAttachmentDebitBypassAuthorized(
            BypassContext(false, PdfSigningApiController.AttachmentDebitBypassHeader, BypassKey), BypassKey));
    }

    [Fact]
    public void Bypass_with_a_wrong_key_is_refused()
    {
        Assert.False(PdfSigningApiController.IsAttachmentDebitBypassAuthorized(
            BypassContext(true, PdfSigningApiController.AttachmentDebitBypassHeader, "wrong"), BypassKey));
    }

    [Fact]
    public void Bypass_header_is_not_allowed_by_CORS()
    {
        var allow = File.ReadAllLines(Path.Combine(RepoRoot(), "Program.cs"))
            .Single(l => l.Contains("Access-Control-Allow-Headers", StringComparison.Ordinal) && l.Contains("Headers[", StringComparison.Ordinal));
        Assert.DoesNotContain("Bypass", allow, StringComparison.OrdinalIgnoreCase);
    }

    // ── Client IP behind Cloudflare (#78 A4, #106) ──────────────────────────────────────────────

    // cloudflared at 172.18.0.5 or in 10.0.1.0/24; Traefik at 172.18.0.2.
    private static readonly ClientIpTrust Tunnel = new(
        TrustedProxies.Parse("172.18.0.5", "10.0.1.0/24"), TrustedProxies.Parse("172.18.0.2"));

    private static DefaultHttpContext FromPeer(string peer, string? cf)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (cf != null) context.Request.Headers["CF-Connecting-IP"] = cf;
        return context;
    }

    [Fact]
    public void Behind_Cloudflare_the_client_is_CF_Connecting_IP_not_the_tunnel()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(FromPeer("172.18.0.5", "203.0.113.7"), Tunnel));
        Assert.Equal(IPAddress.Parse("203.0.113.8"), ClientIp.AddressOf(FromPeer("10.0.1.9", "203.0.113.8"), Tunnel));
    }

    [Fact]
    public void CF_Connecting_IP_from_a_private_peer_that_is_not_a_trusted_proxy_is_ignored()
    {
        // Caddy, another container, anything in the Docker network that is not the tunnel.
        Assert.Equal(IPAddress.Parse("172.18.0.99"), ClientIp.AddressOf(FromPeer("172.18.0.99", "203.0.113.7"), Tunnel));
    }

    [Fact]
    public void A_reverse_proxy_alone_does_not_vouch_for_CF_Connecting_IP()
    {
        // Traefik forwards whatever reached it, tunnel or not: from it, the header counts only when
        // the request it forwards came from cloudflared.
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        Assert.Equal(IPAddress.Parse("172.18.0.2"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task Through_the_reverse_proxy_a_request_from_cloudflared_is_believed()
    {
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            // UseForwardedHeaders applied Traefik's X-Forwarded-For: the request came from cloudflared.
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task Through_the_reverse_proxy_a_request_from_elsewhere_is_not_believed()
    {
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("198.51.100.1"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public void Empty_TRUSTED_CF_PROXIES_ignores_the_header()
    {
        var trust = ClientIpTrust.From(Config(("TRUSTED_CF_PROXIES", ""), ("TRUSTED_PROXIES", "172.18.0.5")));
        Assert.Equal(IPAddress.Parse("172.18.0.5"), ClientIp.AddressOf(FromPeer("172.18.0.5", "203.0.113.7"), trust));
    }

    private static Microsoft.AspNetCore.Builder.ForwardedHeadersOptions Forwarded(string? proxies, string? networks = null, string? known = null)
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        ClientIpTrust.ApplyTo(options, TrustedProxies.Parse(proxies), networks, known);
        return options;
    }

    [Fact]
    public void TRUSTED_PROXIES_none_trusts_no_X_Forwarded_For_at_all()
    {
        var options = Forwarded("none");
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void TRUSTED_PROXIES_is_what_X_Forwarded_For_is_accepted_from()
    {
        var options = Forwarded("172.18.0.2, 10.9.0.0/16");
        Assert.Equal(new[] { IPAddress.Parse("172.18.0.2") }, options.KnownProxies);
        Assert.Single(options.KnownNetworks);
    }

    [Fact]
    public void Unset_TRUSTED_PROXIES_keeps_the_private_range_default()
    {
        var options = Forwarded(null);
        Assert.Contains(options.KnownNetworks, n => n.Prefix.Equals(IPAddress.Parse("172.16.0.0")) && n.PrefixLength == 12);
    }

    [Fact]
    public void An_explicit_ForwardedHeaders_list_wins()
    {
        var options = Forwarded("none", networks: "10.1.0.0/16", known: "172.18.0.9");
        Assert.Equal(new[] { IPAddress.Parse("172.18.0.9") }, options.KnownProxies);
        Assert.Single(options.KnownNetworks);
    }

    [Fact]
    public void Without_trusted_proxies_CF_Connecting_IP_is_never_believed()
    {
        var context = FromPeer("172.18.0.5", "203.0.113.7");
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        Assert.Equal("172.18.0.5", ClientIp.Of(context));
    }

    [Fact]
    public void A_spoofed_X_Original_For_does_not_make_a_peer_trusted()
    {
        var context = FromPeer("93.184.216.34", "203.0.113.7");
        context.Request.Headers["X-Original-For"] = "172.18.0.5";
        Assert.Equal(IPAddress.Parse("93.184.216.34"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task The_socket_peer_is_the_one_before_forwarded_headers_rewrote_it()
    {
        var context = FromPeer("172.18.0.5", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            // What UseForwardedHeaders does with X-Forwarded-For.
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public void An_IPv4_mapped_peer_matches_an_IPv4_proxy_entry()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(FromPeer("::ffff:172.18.0.5", "203.0.113.7"), Tunnel));
    }

    [Fact]
    public void Trusted_proxies_that_do_not_parse_fail_startup()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("not-an-ip"));
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("10.0.0.0/40"));
    }

    [Fact]
    public void None_declares_no_proxy_on_purpose()
    {
        var none = TrustedProxies.Parse("none");
        Assert.True(none.IsEmpty);
        Assert.True(none.DeclaredNone);
        Assert.False(TrustedProxies.Parse("").DeclaredNone);
    }

    /// <summary>
    /// One helper for every client address (#78): the consent evidence (AccountController), the
    /// token IP whitelist (authentication handler, controller fallback) and the rate limits.
    /// A direct read of the peer address elsewhere is the proxy's address behind Cloudflare.
    /// </summary>
    [Fact]
    public void Client_address_is_read_through_ClientIp_only()
    {
        var root = RepoRoot();
        var offenders = new[] { "Controllers", "Services", "Middleware", "Filters" }
            .SelectMany(dir => Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => Path.GetFileName(f) != "ClientIp.cs")
            .Where(f => File.ReadAllText(f).Contains("Connection.RemoteIpAddress", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
