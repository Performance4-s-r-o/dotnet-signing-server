using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Logging;

public static class LoggingEvents
{
    public static readonly EventId AuthFailed = new(1000, nameof(AuthFailed));
    public static readonly EventId CreditsInsufficient = new(1001, nameof(CreditsInsufficient));
    public static readonly EventId ApiError = new(1002, nameof(ApiError));
    public static readonly EventId TokenCreated = new(2000, nameof(TokenCreated));
    public static readonly EventId TokenRevoked = new(2001, nameof(TokenRevoked));
    public static readonly EventId TokenDeleted = new(2002, nameof(TokenDeleted));
}
