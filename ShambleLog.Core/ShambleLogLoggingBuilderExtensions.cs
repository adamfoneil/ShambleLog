using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public static class ShambleLogLoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext>(
        this ILoggingBuilder builder,
        PurgeServiceOptions? purgeServiceOptions = null)
        where TContext : DbContext, ILoggingDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddScoped<ILoggingDbContext>(services => services.GetRequiredService<TContext>());

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

        return builder.AddShambleLog<TContext, LogEntry>();
    }

    public static ILoggingBuilder AddShambleLog<TContext, TLogEntry>(this ILoggingBuilder builder)
        where TContext : DbContext, ILoggingDbContext<TLogEntry>
        where TLogEntry : LogEntry, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<ILoggingDbContext<TLogEntry>>(services => services.GetRequiredService<TContext>());
        builder.Services.TryAddScoped<ILogEntryStore>(services =>
            new LogEntryStore<TLogEntry>(services.GetRequiredService<ILoggingDbContext<TLogEntry>>()));
        builder.Services.TryAddSingleton<LogEntryQueue>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ShambleLogLoggerProvider>());
        builder.Services.TryAddSingleton<LogEntryWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<LogEntryWriter>());
        return builder;
    }
}
