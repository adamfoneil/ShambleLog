using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

public static class ShambleLogLoggingBuilderExtensions
{
    public static ILoggingBuilder AddShambleLog<TContext>(this ILoggingBuilder builder)
        where TContext : DbContext, ILoggingDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<ILoggingDbContext>(services => services.GetRequiredService<TContext>());
        builder.Services.TryAddSingleton<LogEntryQueue>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ShambleLogLoggerProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LogEntryWriter>());
        return builder;
    }
}
