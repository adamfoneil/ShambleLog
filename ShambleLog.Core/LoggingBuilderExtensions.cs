using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Queries;

namespace ShambleLog.Core;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext, TLogQuery>(
        this ILoggingBuilder builder,
        Action<ShambleLogBuilder>? configure = null)
        where TContext : DbContext, ILoggingDbContext
        where TLogQuery : LogQuery
        => Registration.Add<TContext, TLogQuery>(builder, configure);
}
