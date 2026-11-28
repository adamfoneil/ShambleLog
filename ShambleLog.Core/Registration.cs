using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Queries;

namespace ShambleLog.Core;

internal static class Registration
{
    public static ILoggingBuilder Add<TContext, TLogQuery>(
        ILoggingBuilder builder,
        Action<ShambleLogBuilder>? configure)
        where TContext : DbContext, ILoggingDbContext
        where TLogQuery : LogQuery
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<ILoggingDbContext>(services => services.GetRequiredService<TContext>());
        builder.Services.TryAddScoped<CategoryLevelGate>();
        builder.Services.TryAddSingleton<SuppressedCategoryTracker>();
        builder.Services.TryAddSingleton<LogEntryPipeline>();
        builder.Services.TryAddSingleton<LogEntryQueue>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<LoggerFilterOptions>, CategoryLevelFilterOptions>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ShambleLogLoggerProvider>());
        builder.Services.TryAddSingleton<ICategoryLevelInitializer, CategoryLevelInitializer>();
        builder.Services.TryAddSingleton<LogEntryWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<LogEntryWriter>());

        builder.Services.AddTransient<CategorySummaryQuery>();
        builder.Services.AddTransient<CategoryLevelQuery>();
        builder.Services.AddTransient<ScopedQueryRunner>();
        builder.Services.AddTransient<EFCoreMetricsQuery>();
        builder.Services.AddTransient<LogQuery, TLogQuery>();

        if (configure is not null)
        {
            var shambleBuilder = new ShambleLogBuilder(builder.Services);
            configure(shambleBuilder);
            foreach (var component in shambleBuilder.Components)
                builder.Services.AddSingleton(new LogEntryPipelineComponent(component));
        }

        return builder;
    }
}
