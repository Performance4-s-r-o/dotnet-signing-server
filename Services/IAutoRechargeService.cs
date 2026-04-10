using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IAutoRechargeService
{
    Task<AutoRechargeResult> TryAutoRechargeAsync(Guid userId);
    Task EnableAsync(User user, int quantity, decimal pricePer100);
    Task DisableAsync(User user);
    Task DisableByTokenAsync(string cancelToken);
}

public class AutoRechargeResult
{
    public bool Success { get; set; }
    public int CreditsAdded { get; set; }
    public string? Error { get; set; }
}
