using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public sealed class LogEntryWriter(LogEntryQueue queue, IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stoppingToken))
            {
                queue.SuppressLogging = true;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<ILogEntryStore>();
                    await store.SaveAsync(entry, stoppingToken);
                }
                finally
                {
                    queue.SuppressLogging = false;
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

internal interface ILogEntryStore
{
    Task SaveAsync(LogEntry entry, CancellationToken cancellationToken);
}

internal sealed class LogEntryStore<TLogEntry>(ILoggingDbContext<TLogEntry> context) : ILogEntryStore
    where TLogEntry : LogEntry, new()
{
    public async Task SaveAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        var rules = await context.LogCategoryLevels.AsNoTracking().ToListAsync(cancellationToken);
        var rule = rules
            .Where(item => !string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase)
                && entry.Category.StartsWith(item.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Category.Length)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? rules.Where(item => string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .FirstOrDefault();

        if (rule is not null && (rule.Level == LogLevel.None || entry.Level < rule.Level))
            return;

        var persistedEntry = new TLogEntry { Timestamp = entry.Timestamp };
        persistedEntry.SetProperties(entry.Category, entry.Level, entry.Template, entry.Message, entry.Exception, entry.Properties);
        context.LogEntries.Add(persistedEntry);
        await context.SaveChangesAsync(cancellationToken);
    }
}
