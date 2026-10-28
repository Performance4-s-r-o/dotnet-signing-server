namespace DotNetSigningServer.Services;

/// <summary>The card (or Link account) on file, as the billing page shows it.</summary>
public record SavedPaymentMethod(
    string Type,
    string? Brand = null,
    string? Last4 = null,
    long? ExpMonth = null,
    long? ExpYear = null,
    string? LinkEmail = null);
