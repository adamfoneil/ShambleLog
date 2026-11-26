using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using System.Diagnostics;

namespace ShambleLog.Core;

public sealed class LogEntryWriter(LogEntryQueue queue, IServiceScopeFactory scopeFactory) : BackgroundService
{
    private double _ingestionRate;

    public double IngestionRate => Volatile.Read(ref _ingestionRate);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetService<ICategoryLevelInitializer>();
        if (initializer is not null)
            await initializer.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long writtenCount = 0;
        double activeSeconds = 0;
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var started = Stopwatch.GetTimestamp();
                LogEntryContext? context = null;
                queue.SuppressLogging = true;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var gate = scope.ServiceProvider.GetRequiredService<CategoryLevelGate>();
                    if (!await gate.IsEnabledAsync(entry, stoppingToken))
                        continue;

                    var pipeline = scope.ServiceProvider.GetRequiredService<LogEntryPipeline>();
                    context = new LogEntryContext(entry, scope.ServiceProvider, stoppingToken);
                    await pipeline.Invoke(context);
                }
                finally
                {
                    queue.SuppressLogging = false;
                    if (context?.IsPersisted == true)
                    {
                        writtenCount++;
                        activeSeconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
                        Volatile.Write(ref _ingestionRate, activeSeconds > 0 ? writtenCount / activeSeconds : 0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            queue.Complete(ex);
            Console.Error.WriteLine($"ShambleLog: failed to persist log entries: {ex}");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        if (ExecuteTask is not null)
            await ExecuteTask.WaitAsync(cancellationToken);
    }
}

internal sealed class CategoryLevelGate(ILoggingDbContext context)
{
    public async Task<bool> IsEnabledAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        var rules = await context.LogCategoryLevels.AsNoTracking().ToListAsync(cancellationToken);
        var minimumLevel = FindMinimumLevel(rules, entry.Category);
        return minimumLevel is null || (minimumLevel != LogLevel.None && entry.Level >= minimumLevel);
    }

    private static LogLevel? FindMinimumLevel(IEnumerable<CategoryLevel> rules, string category)
    {
        var rule = rules
            .Where(item => !string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase)
                && category.StartsWith(item.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Category.Length)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? rules.Where(item => string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .FirstOrDefault();
        return rule?.Level;
    }
}
