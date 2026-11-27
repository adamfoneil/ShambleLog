using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;
using ShambleLog.RCL;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class ScopedQueryRunnerTests
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ConcurrentViewerQueries_UseSeparateDisposedContexts()
    {
        var contexts = new ConcurrentBag<TestDbContext>();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContextFactory<TestDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ILoggingDbContext>(provider =>
        {
            var context = provider.GetRequiredService<TestDbContext>();
            contexts.Add(context);
            return context;
        });
        services.AddTransient<LogQuery, ClientSideLogQuery>();
        services.AddTransient<CategorySummaryQuery>();
        services.AddTransient<EFCoreSummaryQuery>();
        await using var provider = services.BuildServiceProvider();
        await using var circuit = provider.CreateAsyncScope();
        var runner = new ScopedQueryRunner(circuit.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task WaitForOverlap()
        {
            if (Interlocked.Increment(ref started) == 4)
                ready.SetResult();
            await release.Task;
        }

        var firstLog = runner.ExecuteAsync<LogQuery, LogEntry[]>(async query =>
        {
            await WaitForOverlap();
            return await query.ExecuteAsync(new LogQuery.Criteria(), 0, 50);
        });
        var secondLog = runner.ExecuteAsync<LogQuery, LogEntry[]>(async query =>
        {
            await WaitForOverlap();
            return await query.ExecuteAsync(new LogQuery.Criteria(), 0, 50);
        });
        var categories = runner.ExecuteAsync<CategorySummaryQuery, CategorySummaryQuery.Result[]>(async query =>
        {
            await WaitForOverlap();
            return await query.ExecuteAsync();
        });
        var efCore = runner.ExecuteAsync<EFCoreSummaryQuery, EFCoreSummaryQuery.Result[]>(async query =>
        {
            await WaitForOverlap();
            return await query.ExecuteAsync();
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(4, contexts.Select(context => context.ContextId.InstanceId).Distinct().Count());
        release.SetResult();
        await Task.WhenAll(firstLog, secondLog, categories, efCore);
        foreach (var context in contexts)
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = context.Model);
    }

    [TestMethod]
    public async Task FailedQuery_DisposesItsScope()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        var runner = new ScopedQueryRunner(provider.GetRequiredService<IServiceScopeFactory>());
        TestDbContext? context = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            runner.ExecuteAsync<TestDbContext, int>(db =>
            {
                context = db;
                throw new InvalidOperationException("query failed");
            }));
        Assert.IsNotNull(context);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = context.Model);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IEFCoreLoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;
        public DbSet<EFCoreEntry> EFCoreEntries { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => JsonSerializer.Serialize(properties, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, (JsonSerializerOptions?)null)!);
            modelBuilder.Entity<EFCoreEntry>().HasKey(entry => entry.LogEntryId);
        }
    }
}
