extern alias PostgreSQL;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PgCore = PostgreSQL::ShambleLog.Core;
using PgEntities = PostgreSQL::ShambleLog.Core.Entities;
using PgConfigurations = PostgreSQL::ShambleLog.PostgreSQL.Configurations;
using PgLogQuery = PostgreSQL::ShambleLog.PostgreSQL.PostgreSQLLogQuery;

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

    private sealed class ExposedPostgreSQLLogQuery(PgCore.ILoggingDbContext db) : PgLogQuery(db)
    {
        public (IQueryable<PgEntities.LogEntry> Query, Func<PgEntities.LogEntry, bool>? ClientFilter) Apply(
            IQueryable<PgEntities.LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties) =>
            FilterProperties(query, properties);
    }

    private sealed class PostgreSQLContext(DbContextOptions<PostgreSQLContext> options) : DbContext(options), PgCore.ILoggingDbContext
    {
        public DbSet<PgEntities.LogEntry> LogEntries { get; set; } = null!;
        public DbSet<PgEntities.CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new PgConfigurations.LogEntryConfiguration());
            modelBuilder.ApplyConfiguration(new PgConfigurations.CategoryLevelConfiguration());
            modelBuilder.ApplyConfiguration(new PgConfigurations.EFCoreEntryConfiguration());
        }
    }
}
