namespace DotNetSigningServer.Services.Settings;

/// <summary>Type of a runtime setting, as the settings service names it.</summary>
public enum RuntimeSettingType
{
    /// <summary>Whole number.</summary>
    Int,

    /// <summary>Whole number in the setting's unit (minutes here).</summary>
    Duration,

    /// <summary>Switch; only ever used with <see cref="RuntimeSettingFailMode.Closed"/> here.</summary>
    Bool,
}

/// <summary>What a setting falls back to while the settings service cannot be read.</summary>
public enum RuntimeSettingFailMode
{
    /// <summary>The last value read, before the first read the default in code.</summary>
    LastKnown,

    /// <summary>
    /// A kill switch: counts only while fresh (<c>closed_max_stale_seconds</c>); otherwise, and before the
    /// first successful read, it is closed (<c>false</c>). The default in code is never used.
    /// </summary>
    Closed,
}

/// <summary>A setting this product reads, with its default in code.</summary>
/// <param name="Key">Key in the settings service; the env override is the same name in upper case.</param>
/// <param name="Default">Default in code (ignored for <see cref="RuntimeSettingFailMode.Closed"/>).</param>
public sealed record RuntimeSettingDefinition(string Key, RuntimeSettingType Type, int Default, RuntimeSettingFailMode FailMode)
{
    /// <summary>The env variable that overrides the setting: the key in upper case.</summary>
    public string EnvName => Key.ToUpperInvariant();
}

/// <summary>
/// The runtime settings of this product (<c>GET /v1/settings</c>). Values are tuning values only;
/// secrets, addresses and anything checked at start stay in env.
/// </summary>
public static class RuntimeSettingKeys
{
    /// <summary>Presigns waiting for /api/sign per account; 0 (env only) = no limit.</summary>
    public const string MaxPendingPresignsPerUser = "max_pending_presigns_per_user";

    /// <summary>Stored templates per account; 0 (env only) = no limit.</summary>
    public const string MaxTemplatesPerUser = "max_templates_per_user";

    /// <summary>Live API tokens per account; 0 (env only) = no limit.</summary>
    public const string MaxApiTokensPerUser = "max_api_tokens_per_user";

    /// <summary>Minutes since the last password or code confirmation before a sensitive action.</summary>
    public const string AuthReauthMinutes = "auth_reauth_minutes";

    /// <summary>Wrong e-mailed codes after which the code is void.</summary>
    public const string AuthOtpMaxFailedAttempts = "auth_otp_max_failed_attempts";

    /// <summary>Sign-in and 2FA attempts per account within <see cref="AuthAccountWindowMinutes"/>.</summary>
    public const string AuthAccountMaxAttempts = "auth_account_max_attempts";

    /// <summary>Window of <see cref="AuthAccountMaxAttempts"/>, minutes.</summary>
    public const string AuthAccountWindowMinutes = "auth_account_window_minutes";

    /// <summary>Kill switch of every /api/ai/* route (closed).</summary>
    public const string AiEnabled = "ai_enabled";

    public static readonly IReadOnlyList<RuntimeSettingDefinition> All =
    [
        new(MaxPendingPresignsPerUser, RuntimeSettingType.Int, 20, RuntimeSettingFailMode.LastKnown),
        new(MaxTemplatesPerUser, RuntimeSettingType.Int, 100, RuntimeSettingFailMode.LastKnown),
        new(MaxApiTokensPerUser, RuntimeSettingType.Int, 100, RuntimeSettingFailMode.LastKnown),
        new(AuthReauthMinutes, RuntimeSettingType.Duration, 15, RuntimeSettingFailMode.LastKnown),
        new(AuthOtpMaxFailedAttempts, RuntimeSettingType.Int, 5, RuntimeSettingFailMode.LastKnown),
        new(AuthAccountMaxAttempts, RuntimeSettingType.Int, 30, RuntimeSettingFailMode.LastKnown),
        new(AuthAccountWindowMinutes, RuntimeSettingType.Duration, 15, RuntimeSettingFailMode.LastKnown),
        new(AiEnabled, RuntimeSettingType.Bool, 0, RuntimeSettingFailMode.Closed),
    ];

    private static readonly Dictionary<string, RuntimeSettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>The definition of <paramref name="key"/>; throws for a key this product does not read.</summary>
    public static RuntimeSettingDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown runtime setting");

    /// <summary>Keys that are closed before the first successful read (the client's <c>closedKeys</c>).</summary>
    public static IReadOnlyList<string> ClosedKeys { get; } =
        All.Where(d => d.FailMode == RuntimeSettingFailMode.Closed).Select(d => d.Key).ToArray();
}
