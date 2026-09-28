namespace DotNetSigningServer.Services.Support;

/// <summary>
/// What the service answered to a ticket, kept in the outbox item's <c>RemoteId</c> (the
/// payload is cleared once sent): <c>{id}#{ticket_number}</c>, or just <c>{id}</c> while the
/// ticket is queued in the service.
/// </summary>
public static class SupportTicketReceipt
{
    public const char Separator = '#';

    /// <summary>Same limit as <c>BackofficeOutboxItem.RemoteId</c>.</summary>
    public const int MaxLength = 64;

    public static string? Format(string? requestId, string? ticketNumber)
    {
        var id = string.IsNullOrWhiteSpace(requestId) ? "" : requestId.Trim();
        var number = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim().Replace(Separator.ToString(), "");
        if (string.IsNullOrEmpty(number)) return id.Length == 0 ? null : Cut(id);

        var full = $"{id}{Separator}{number}";
        if (full.Length <= MaxLength) return full;
        // Keep the number, which the user is shown, rather than the request id.
        var numberPart = Separator + number;
        return numberPart.Length <= MaxLength ? numberPart : null;
    }

    /// <summary>The ticket number stored by <see cref="Format"/>; null when there is none.</summary>
    public static string? TicketNumber(string? remoteId)
    {
        if (string.IsNullOrEmpty(remoteId)) return null;
        var at = remoteId.LastIndexOf(Separator);
        if (at < 0 || at == remoteId.Length - 1) return null;
        return remoteId[(at + 1)..];
    }

    private static string Cut(string value) => value.Length <= MaxLength ? value : value[..MaxLength];
}
