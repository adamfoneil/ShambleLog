using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public class RetentionPeriodOptions
{
    public TimeSpan DeleteAfter { get; set; }
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

public class RetentionPeriodService(
    ILogger<RetentionPeriodService> logger,
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionPeriodOptions> options) : BackgroundService
{
    private readonly ILogger<RetentionPeriodService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly RetentionPeriodOptions _options = options.Value;

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
                var cutoff = DateTime.UtcNow - _options.DeleteAfter;
                int deletedCount = await PurgeAsync(scope.ServiceProvider.GetRequiredService<ILoggingDbContext>(), cutoff, stoppingToken);

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

    private static async Task<int> PurgeAsync(ILoggingDbContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        var entriesToDelete = context.LogEntries.Where(entry => entry.Timestamp < cutoff);
        context.LogEntries.RemoveRange(entriesToDelete);
        return await context.SaveChangesAsync(cancellationToken);
    }
}
