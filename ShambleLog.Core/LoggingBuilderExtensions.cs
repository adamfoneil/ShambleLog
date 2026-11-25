using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext>(
        this ILoggingBuilder builder,
        PurgeServiceOptions? purgeServiceOptions = null)
        where TContext : DbContext, ILoggingDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddScoped<ILoggingDbContext>(services => services.GetRequiredService<TContext>());

        return Registration.Add<TContext, LogEntry>(builder, purgeServiceOptions);
    }

    public static ILoggingBuilder AddShambleLog<TContext, TLogEntry>(this ILoggingBuilder builder)
        where TContext : DbContext, ILoggingDbContext<TLogEntry>
        where TLogEntry : LogEntry, new() =>
        Registration.Add<TContext, TLogEntry>(builder, null);

    public static ILoggingBuilder AddShambleLog<TContext, TLogEntry>(
        this ILoggingBuilder builder,
        PurgeServiceOptions? purgeServiceOptions)
        where TContext : DbContext, ILoggingDbContext<TLogEntry>
        where TLogEntry : LogEntry, new() =>
        Registration.Add<TContext, TLogEntry>(builder, purgeServiceOptions);
}
