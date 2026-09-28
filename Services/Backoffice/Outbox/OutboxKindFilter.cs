namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Kinds the dispatcher leaves Pending because their module is not On (e.g. <c>email.</c> while
/// <c>Modules:Email</c> is Off or Shadow). They stay in the outbox untouched — not retried into
/// Dead — and go out once the module is On again.
/// </summary>
public sealed class OutboxKindFilter
{
    public static readonly OutboxKindFilter None = new(Array.Empty<string>());

    public OutboxKindFilter(IReadOnlyList<string> pausedKindPrefixes)
    {
        PausedKindPrefixes = pausedKindPrefixes;
    }

    /// <summary>Kind prefixes that are not sent, e.g. <c>email.</c>.</summary>
    public IReadOnlyList<string> PausedKindPrefixes { get; }
}
