using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;
using ShambleLog.PostgreSQL;
using ShambleLog.PostgreSQL.Configurations;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class PostgreSQLLogQueryTests
{
    [TestMethod]
    public void FilterProperties_TranslatesInclusionAndExclusionToJsonbContainment()
    {
        using var db = new PostgreSQLContext(new DbContextOptionsBuilder<PostgreSQLContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        var query = new ExposedPostgreSQLLogQuery(db);
        var (filtered, clientFilter) = query.Apply(db.LogEntries,
        [
            ("TraceId", JsonSerializer.SerializeToElement("abc"), false),
            ("Method", JsonSerializer.SerializeToElement("Get"), true)
        ]);

        var sql = filtered.ToQueryString();
        Assert.AreEqual(2, sql.Split("@>").Length - 1);
        StringAssert.Contains(sql, "NOT (");
        StringAssert.Contains(sql, "TraceId");
        StringAssert.Contains(sql, "abc");
        StringAssert.Contains(sql, "Method");
        StringAssert.Contains(sql, "Get");
        Assert.IsNull(clientFilter);
    }

    [TestMethod]
    public async Task ExecuteAsync_TranslatesShorthandToStringContainment()
    {
        await using var db = new PostgreSQLContext(new DbContextOptionsBuilder<PostgreSQLContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        var query = new CapturingPostgreSQLLogQuery(db);
        var criteria = new ShambleLog.Core.Queries.LogQuery.Criteria { PropertiesContain = "UserId:123, Method:-Get" };

        await Assert.ThrowsExactlyAsync<FilterCapturedException>(() => query.ExecuteAsync(criteria, 0, 10));
        StringAssert.Contains(query.Sql!, "@>");
        StringAssert.Contains(query.Sql!, "UserId");
        StringAssert.Contains(query.Sql!, "123");
        StringAssert.Contains(query.Sql!, "NOT (");
        StringAssert.Contains(query.Sql!, "Method");
        StringAssert.Contains(query.Sql!, "Get");
    }

    private sealed class FilterCapturedException : Exception;

    private sealed class CapturingPostgreSQLLogQuery(ILoggingDbContext db) : PostgreSQLLogQuery(db)
    {
        public string? Sql { get; private set; }

        protected override (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
            IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties)
        {
            Assert.AreEqual(JsonValueKind.String, properties[0].Value.ValueKind);
            Sql = base.FilterProperties(query, properties).Query.ToQueryString();
            throw new FilterCapturedException();
        }
    }

    private sealed class ExposedPostgreSQLLogQuery(ILoggingDbContext db) : PostgreSQLLogQuery(db)
    {
        public (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) Apply(
            IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties) =>
            FilterProperties(query, properties);
    }

    private sealed class PostgreSQLContext(DbContextOptions<PostgreSQLContext> options) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new LogEntryConfiguration());
            modelBuilder.ApplyConfiguration(new CategoryLevelConfiguration());
            modelBuilder.ApplyConfiguration(new EFCoreEntryConfiguration());
        }
    }
}
