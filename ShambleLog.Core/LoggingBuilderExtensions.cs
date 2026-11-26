using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext>(
        this ILoggingBuilder builder,
        Action<ShambleLogBuilder>? configure = null)
        where TContext : DbContext, ILoggingDbContext
        => Registration.Add<TContext>(builder, configure);
}
