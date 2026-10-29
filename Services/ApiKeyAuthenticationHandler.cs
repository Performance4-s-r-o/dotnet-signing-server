using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Authenticates requests that carry an API bearer token in the Authorization header.
/// Runs early in the pipeline (via UseAuthentication) so throttling middleware can
/// read ClaimsPrincipal to apply per-user limits.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    private readonly IApiAuthService _apiAuth;
    private readonly DotNetSigningServer.Options.PortalTrustOptions _portalTrust;
    private readonly MissingTrustedCallerFlag? _missingFlag;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiAuthService apiAuth,
        DotNetSigningServer.Options.PortalTrustOptions portalTrust,
        MissingTrustedCallerFlag? missingFlag = null)
        : base(options, logger, encoder)
    {
        _apiAuth = apiAuth;
        _portalTrust = portalTrust;
        _missingFlag = missingFlag;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers["Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var origin = Request.Headers["Origin"].ToString();
        // The client behind Cloudflare, not the tunnel (#78): the token IP whitelist is about the caller.
        var ip = DotNetSigningServer.Middleware.ClientIp.AddressOf(Context);

        var validation = await _apiAuth.ValidateTokenDetailedAsync(authHeader, origin, ip);
        if (validation == null)
        {
            return AuthenticateResult.NoResult();
        }
        var user = validation.User;

        // A trusted account's token works on the internal listener only (#78). On the public one it
        // is refused like an unknown token, so the gate answers 401 before any body is read.
        var kind = PortalTrust.Classify(_portalTrust, Context.Connection.LocalPort, user.TrustedCaller, validation.IsBrowserToken);
        if (kind == PortalCallKind.Refused)
        {
            Logger.LogWarning(Logging.LoggingEvents.AuthFailed,
                "Portal token {TokenId} refused on port {Port}: it is accepted on the internal listener only",
                validation.TokenId, Context.Connection.LocalPort);
            return AuthenticateResult.NoResult();
        }

        if (_missingFlag?.ShouldWarn(_portalTrust, Context.Connection.LocalPort, kind, validation.IsBrowserToken,
                user.Id, Request.Headers, DateTimeOffset.UtcNow) == true)
        {
            Logger.LogWarning(Logging.LoggingEvents.AuthFailed,
                "Account {UserId} sends trusted caller headers on the internal port but is not marked TrustedCaller: "
                + "the headers are ignored, tenant checks are off and per-account limits apply. Set the flag in the administration.",
                user.Id);
        }

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("sub", user.Id.ToString()),
            // Which token and what kind (#80): ApiGateMiddleware limits browser tokens to their
            // scope, the operation runner to their credit budget.
            new Claim(SessionClaims.TokenId, validation.TokenId.ToString()),
            new Claim(SessionClaims.TokenKind, validation.IsBrowserToken ? SessionClaims.BrowserTokenKind : SessionClaims.ServerTokenKind),
        };
        if (kind == PortalCallKind.Trusted) claims.Add(new Claim(PortalTrust.TrustedCallerClaim, "true"));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
