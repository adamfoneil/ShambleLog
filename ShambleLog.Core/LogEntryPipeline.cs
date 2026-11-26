using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public delegate Task LogEntryDelegate(LogEntryContext context);

public interface ILogEntryMiddleware
{
    Task InvokeAsync(LogEntryContext context, LogEntryDelegate next);
}

public sealed class LogEntryContext(LogEntry entry, IServiceProvider services, CancellationToken cancellationToken)
{
    public LogEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));
    public IServiceProvider Services { get; } = services ?? throw new ArgumentNullException(nameof(services));
    public CancellationToken CancellationToken { get; } = cancellationToken;
}

public sealed class ShambleLogBuilder
{
    private readonly List<Func<LogEntryDelegate, LogEntryDelegate>> _components = [];

    internal ShambleLogBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    internal IReadOnlyList<Func<LogEntryDelegate, LogEntryDelegate>> Components => _components;

    public ShambleLogBuilder Use(Func<LogEntryContext, LogEntryDelegate, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _components.Add(next => context => middleware(context, next));
        return this;
    }

    public ShambleLogBuilder Use<TMiddleware>() where TMiddleware : class, ILogEntryMiddleware
    {
        Services.TryAddScoped<TMiddleware>();
        _components.Add(next => context => context.Services.GetRequiredService<TMiddleware>().InvokeAsync(context, next));
        return this;
    }

    public ShambleLogBuilder UseWhen(Func<LogEntryContext, bool> predicate, Action<ShambleLogBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(configure);

        var branchBuilder = new ShambleLogBuilder(Services);
        configure(branchBuilder);
        var branchComponents = branchBuilder.Components.ToArray();

        _components.Add(next =>
        {
            var branch = Build(branchComponents, next);
            return context => predicate(context) ? branch(context) : next(context);
        });
        return this;
    }

    /// <summary>
    /// Runs the branch for categories starting with <paramref name="categoryPrefix"/> (ordinal, case-insensitive),
    /// then continues with the rest of the pipeline.
    /// </summary>
    public ShambleLogBuilder UseForCategory(string categoryPrefix, Action<ShambleLogBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(categoryPrefix);
        return UseWhen(context => context.Entry.Category.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase), configure);
    }

    internal static LogEntryDelegate Build(IEnumerable<Func<LogEntryDelegate, LogEntryDelegate>> components, LogEntryDelegate terminal)
    {
        var pipeline = terminal;
        foreach (var component in components.Reverse())
            pipeline = component(pipeline);
        return pipeline;
    }
}

internal sealed record LogEntryPipelineComponent(Func<LogEntryDelegate, LogEntryDelegate> Component);

internal sealed class LogEntryPipeline(IEnumerable<LogEntryPipelineComponent> components)
{
    public LogEntryDelegate Invoke { get; } = ShambleLogBuilder.Build(components.Select(item => item.Component), SaveAsync);

    private static async Task SaveAsync(LogEntryContext context)
    {
        var dbContext = context.Services.GetRequiredService<ILoggingDbContext>();
        dbContext.LogEntries.Add(context.Entry);
        await dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
