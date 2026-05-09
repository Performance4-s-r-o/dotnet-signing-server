using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Background service that periodically cleans up abandoned presign data
/// (SigningData records and their temp PDF files) older than 30 minutes.
/// </summary>
public class PresignCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PresignCleanupService> _logger;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxPresignAge = TimeSpan.FromMinutes(30);

    public PresignCleanupService(IServiceProvider serviceProvider, ILogger<PresignCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAbandonedPresignsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during presign cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupAbandonedPresignsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var cutoff = DateTime.UtcNow - MaxPresignAge;
        var staleRecords = await dbContext.SigningData
            .Where(s => s.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (staleRecords.Count == 0) return;

        foreach (var record in staleRecords)
        {
            try
            {
                if (!string.IsNullOrEmpty(record.PresignedPdfPath) && File.Exists(record.PresignedPdfPath))
                {
                    File.Delete(record.PresignedPdfPath);
                    _logger.LogInformation("Deleted abandoned presign temp file: {Path}", record.PresignedPdfPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete presign temp file: {Path}", record.PresignedPdfPath);
            }

            dbContext.SigningData.Remove(record);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Cleaned up {Count} abandoned presign record(s)", staleRecords.Count);
    }

}
