using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext>(
        this ILoggingBuilder builder,
        PurgeServiceOptions? purgeServiceOptions = null)
        where TContext : DbContext, ILoggingDbContext =>
        Registration.Add<TContext>(builder, purgeServiceOptions, null);

    /// <summary>
    /// Registers ShambleLog and appends middleware that runs, in order, for each entry passing the category-level gate.
    /// Each call appends its middleware to the pipeline.
    /// </summary>
    public static ILoggingBuilder AddShambleLog<TContext>(
        this ILoggingBuilder builder,
        Action<ShambleLogBuilder> configure,
        PurgeServiceOptions? purgeServiceOptions = null)
        where TContext : DbContext, ILoggingDbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Registration.Add<TContext>(builder, purgeServiceOptions, configure);
    }
}
