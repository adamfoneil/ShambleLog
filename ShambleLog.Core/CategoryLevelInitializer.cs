using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

internal interface ICategoryLevelInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

internal sealed class CategoryLevelInitializer<TLogEntry>(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    LogEntryQueue queue,
    ILogger<CategoryLevelInitializer<TLogEntry>> logger) : ICategoryLevelInitializer
    where TLogEntry : LogEntry
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var section = services.GetService<IConfiguration>()?.GetSection("Logging:LogLevel");
        if (section is null)
            return;

        var configured = new List<CategoryLevel>();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is null || !Enum.TryParse<LogLevel>(child.Value, true, out var level)
                || !Enum.GetNames<LogLevel>().Contains(child.Value, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid log level '{child.Value}' for configuration key '{child.Path}'.");
            configured.Add(new CategoryLevel { Category = child.Key, Level = level });
        }

        if (configured.Count == 0)
            return;

        var conflicts = new List<(string Category, LogLevel Configured, LogLevel Existing)>();
        queue.SuppressLogging = true;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ILoggingDbContext<TLogEntry>>();
            var existing = await context.LogCategoryLevels.AsNoTracking().ToListAsync(cancellationToken);
            var added = false;
            foreach (var rule in configured)
            {
                var match = existing.Where(item => string.Equals(item.Category, rule.Category, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Category, StringComparer.Ordinal).FirstOrDefault();
                if (match is null)
                {
                    context.LogCategoryLevels.Add(rule);
                    added = true;
                }
                else if (match.Level != rule.Level)
                    conflicts.Add((rule.Category, rule.Level, match.Level));
            }
            if (added)
                await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            queue.SuppressLogging = false;
        }

        foreach (var conflict in conflicts)
            logger.LogWarning("ShambleLog category {Category} is configured as {ConfiguredLevel}, but the database level {DatabaseLevel} takes precedence",
                conflict.Category, conflict.Configured, conflict.Existing);
    }
}

internal sealed class CategoryLevelFilterOptions(IServiceProvider services) : IConfigureOptions<LoggerFilterOptions>
{
    public void Configure(LoggerFilterOptions options)
    {
        if (services.GetService<IConfiguration>()?.GetSection("Logging:LogLevel").GetChildren().Any() == true)
            options.Rules.Add(new LoggerFilterRule(typeof(ShambleLogLoggerProvider).FullName, null, LogLevel.Trace, null));
    }
}
