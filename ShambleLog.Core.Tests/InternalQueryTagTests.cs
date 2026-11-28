using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class InternalQueryTagTests
{
    [TestMethod]
    public async Task SummaryAndCategoryQueries_AreTagged()
    {
        var interceptor = new QueryCaptureInterceptor();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<TestDbContext>(options => options
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(interceptor));
        services.AddScoped<ILoggingDbContext>(provider => provider.GetRequiredService<TestDbContext>());
        await using var provider = services.BuildServiceProvider();
        var categories = new CategoryLevelQuery(provider.GetRequiredService<IServiceScopeFactory>());

        await categories.SaveAsync(new CategoryLevel { Category = "Example", Level = LogLevel.Information });
        AssertTagged(interceptor.TakeSingle(), nameof(CategoryLevel));

        var levels = await categories.ExecuteAsync();
        Assert.AreEqual("Example", Assert.ContainsSingle(levels).Category);
        AssertTagged(interceptor.TakeSingle(), nameof(CategoryLevel));

        await categories.DeleteAsync("Example");
        AssertTagged(interceptor.TakeSingle(), nameof(CategoryLevel));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var results = await new EFCoreSummaryQuery(context).ExecuteAsync();
        Assert.IsEmpty(results);
        AssertTagged(interceptor.TakeSingle(), nameof(EFCoreEntry));
    }

    [TestMethod]
    public async Task EFCoreSummaryQuery_GeneratesTaggedPostgreSql()
    {
        var interceptor = new StopCompilationInterceptor();
        await using var context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .AddInterceptors(interceptor).Options);

        await Assert.ThrowsExactlyAsync<QueryCapturedException>(() => new EFCoreSummaryQuery(context).ExecuteAsync());
        Assert.IsNotNull(interceptor.Query);
        interceptor.Stop = false;
        var sql = ((IQueryable<EFCoreEntry>)context.EFCoreEntries).Provider.CreateQuery(interceptor.Query).ToQueryString();
        StringAssert.Contains(sql, "-- ShambleLog Internal");
        StringAssert.Contains(sql, "FROM \"EFCoreEntries\"");
        StringAssert.Contains(sql, "JOIN \"LogEntries\"");
    }

    private static void AssertTagged(Expression expression, string entityName)
    {
        var roots = new QueryRootVisitor();
        roots.Visit(expression);
        Assert.IsTrue(roots.EntityNames.Contains(entityName));
        StringAssert.Contains(expression.ToString(), "TagWith(\"ShambleLog Internal\")");
    }

    private sealed class QueryRootVisitor : ExpressionVisitor
    {
        public HashSet<string> EntityNames { get; } = [];

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root)
                EntityNames.Add(root.EntityType.ClrType.Name);
            return base.VisitExtension(node);
        }
    }

    private sealed class QueryCapturedException : Exception;

    private sealed class StopCompilationInterceptor : IQueryExpressionInterceptor
    {
        public bool Stop { get; set; } = true;
        public Expression? Query { get; private set; }

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            if (Stop)
            {
                Query = queryExpression;
                throw new QueryCapturedException();
            }
            return queryExpression;
        }
    }

    private sealed class QueryCaptureInterceptor : IQueryExpressionInterceptor
    {
        private readonly ConcurrentQueue<Expression> _queries = new();

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            _queries.Enqueue(queryExpression);
            return queryExpression;
        }

        public Expression TakeSingle()
        {
            Assert.IsTrue(_queries.TryDequeue(out var query));
            Assert.IsTrue(_queries.IsEmpty);
            return query;
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IEFCoreLoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;
        public DbSet<EFCoreEntry> EFCoreEntries { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LogEntry>().Ignore(entry => entry.Properties);
            modelBuilder.Entity<EFCoreEntry>().HasKey(entry => entry.LogEntryId);
        }
    }
}
