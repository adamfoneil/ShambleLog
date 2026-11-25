using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShambleLog.Core;

public class PurgeServiceOptions
{
    public TimeSpan DeleteAfter { get; set; }
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

public class PurgeService(
    ILogger<PurgeService> logger,
    IServiceScopeFactory scopeFactory,
    IOptions<PurgeServiceOptions> options) : BackgroundService
{
    private readonly ILogger<PurgeService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly PurgeServiceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Purging log entries older than {DeleteAfter}", _options.DeleteAfter);
            }
            
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ILoggingDbContext>();
                var cutoff = DateTime.UtcNow - _options.DeleteAfter;
                var entriesToDelete = dbContext.LogEntries.Where(e => e.Timestamp < cutoff);
                dbContext.LogEntries.RemoveRange(entriesToDelete);
                int deletedCount = await dbContext.SaveChangesAsync(stoppingToken);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Deleted {DeletedCount} log entries", deletedCount);
                }                
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ShambleLog: failed to purge log entries");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown, no need to log (and no need to hit the delay)
                return;
            }

            await Task.Delay(_options.Interval, stoppingToken);
        }
    }
}
