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
