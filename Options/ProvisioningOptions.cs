namespace DotNetSigningServer.Options;

/// <summary>
/// Limits of the provisioning API (#131). docker-compose passes the raw names; see
/// <see cref="EnvValues"/>. A value that does not parse, or is negative, stops the start.
/// </summary>
public class ProvisioningOptions
{
    public const string MaxAccountsPerDayVariable = "PROVISIONING_MAX_ACCOUNTS_PER_DAY";
    public const string AlertAccountsPerHourVariable = "PROVISIONING_ALERT_ACCOUNTS_PER_HOUR";

    /// <summary>Accounts one parent may create in 24 h; 0 = no limit.</summary>
    public int MaxAccountsPerDay { get; set; } = 50;

    /// <summary>Accounts created in an hour that raise an ops alert; 0 = no alert.</summary>
    public int AlertAccountsPerHour { get; set; } = 10;

    /// <summary>Calls of the provisioning API per token and minute.</summary>
    public int CallsPerMinute { get; set; } = 30;

    public static ProvisioningOptions From(IConfiguration configuration)
    {
        var options = new ProvisioningOptions
        {
            MaxAccountsPerDay = EnvValues.Int(configuration, MaxAccountsPerDayVariable) ?? 50,
            AlertAccountsPerHour = EnvValues.Int(configuration, AlertAccountsPerHourVariable) ?? 10,
        };
        if (options.MaxAccountsPerDay < 0)
        {
            throw new InvalidOperationException($"{MaxAccountsPerDayVariable} must be 0 or more, got {options.MaxAccountsPerDay}.");
        }
        if (options.AlertAccountsPerHour < 0)
        {
            throw new InvalidOperationException($"{AlertAccountsPerHourVariable} must be 0 or more, got {options.AlertAccountsPerHour}.");
        }
        return options;
    }
}
