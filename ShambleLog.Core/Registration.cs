using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

internal static class Registration
{
    public static ILoggingBuilder Add<TContext, TLogEntry>(ILoggingBuilder builder, PurgeServiceOptions? purgeServiceOptions)
        where TContext : DbContext, ILoggingDbContext<TLogEntry>
        where TLogEntry : LogEntry, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<ILoggingDbContext<TLogEntry>>(services => services.GetRequiredService<TContext>());
        builder.Services.TryAddScoped<ILogEntryStore>(services =>
            new LogEntryStore<TLogEntry>(services.GetRequiredService<ILoggingDbContext<TLogEntry>>()));
        builder.Services.TryAddSingleton<LogEntryQueue>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<LoggerFilterOptions>, CategoryLevelFilterOptions>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ShambleLogLoggerProvider>());
        builder.Services.TryAddSingleton<ICategoryLevelInitializer, CategoryLevelInitializer<TLogEntry>>();
        builder.Services.TryAddSingleton<LogEntryWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<LogEntryWriter>());

        if (purgeServiceOptions is not null)
        {
            var deleteAfter = purgeServiceOptions.DeleteAfter;
            var interval = purgeServiceOptions.Interval;
            builder.Services.Configure<PurgeServiceOptions>(options =>
            {
                options.DeleteAfter = deleteAfter;
                options.Interval = interval;
            });
            builder.Services.AddHostedService<PurgeService>();
        }

        return builder;
    }
}
