namespace DotNetSigningServer.Exceptions;

/// <summary>
/// Thrown by service/domain layer for validation and business rule violations.
/// The Code maps to a SharedStrings.resx key (Error_{Code}) for localized messages.
/// </summary>
public class ApiValidationException : Exception
{
    public string Code { get; }

    public ApiValidationException(string code) : base(code)
    {
        Code = code;
    }

    public ApiValidationException(string code, string? detail)
        : base(detail ?? code)
    {
        Code = code;
    }
}
