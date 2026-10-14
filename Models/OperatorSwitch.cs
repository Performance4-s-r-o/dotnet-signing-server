using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>
/// A lever the operator can pull without a deployment (#73): one row = one switch that is ON.
/// Delete the row to release it. Read through a short cache, so a change takes effect within
/// <see cref="OperatorSwitches.CacheSeconds"/> seconds on every instance.
///
/// Names: <c>feature:find-codes</c>, <c>feature:extend-signature</c>, <c>feature:ai</c> — the
/// feature is refused for everybody — and <c>user:{guid}</c> — one account is refused on /api.
/// </summary>
public class OperatorSwitch
{
    [Key]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Why it was pulled, for whoever finds it later.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class OperatorSwitches
{
    public const int CacheSeconds = 15;

    public static string Feature(string name) => $"feature:{name}";
    public static string User(Guid id) => $"user:{id}";
}
