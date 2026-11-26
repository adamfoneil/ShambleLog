using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class LogQueryTests
{
    [TestMethod]
    public async Task LogQuery_FiltersOrdersAndIncludesCommands()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "first");
        var second = Entry(2, date.AddHours(2), "second");
        var other = Entry(3, date.AddDays(1), "other");
        second.EFCoreEntry = new EFCoreEntry { LogEntryId = 2, CommandText = "select" };
        db.LogEntries.AddRange(first, second, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date) };
        var rows = await query.ExecuteAsync(criteria);
        CollectionAssert.AreEqual(new long[] { 2, 1 }, rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("select", rows[0].EFCoreEntry?.CommandText);
        Assert.IsNull(rows[1].EFCoreEntry);

        criteria.TimeExpression = "asc";
        criteria.Category = "Example";
        criteria.Level = LogLevel.Warning;
        criteria.TemplateContains = "template";
        criteria.MessageContains = "first";
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria)).Id);
        criteria.Category = "Other";
        Assert.IsEmpty(await query.ExecuteAsync(criteria));
    }

    [TestMethod]
    public async Task LogQuery_PropertiesMatchJsonValuesAndExclusions()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date, "first");
        first.Properties = new() { ["TraceId"] = "abc", ["Count"] = 3, ["Method"] = "Get", ["Scopes"] = new[] { "request", "transaction" }, ["Detail"] = new { Name = "test", Extra = 1 } };
        var second = Entry(2, date.AddHours(1), "second");
        second.Properties = new() { ["TraceId"] = "abc", ["Count"] = 3, ["Method"] = "Post" };
        var missing = Entry(3, date.AddHours(2), "missing");
        missing.Properties = new() { ["TraceId"] = "abc", ["Count"] = 3 };
        db.LogEntries.AddRange(first, second, missing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var criteria = new LogQuery.Criteria
        {
            Date = DateOnly.FromDateTime(date),
            PropertiesContain = """{"TraceId":"abc","Count":3,"Method":"-Get"}"""
        };
        var rows = await new LogQuery(db).ExecuteAsync(criteria);
        CollectionAssert.AreEqual(new long[] { 3, 2 }, rows.Select(row => row.Id).ToArray());
        criteria.PropertiesContain = """{"Count":"3"}""";
        Assert.IsEmpty(await new LogQuery(db).ExecuteAsync(criteria));
        criteria.PropertiesContain = """{"Scopes":["request"],"Detail":{"Name":"test"}}""";
        Assert.AreEqual(1L, Assert.ContainsSingle(await new LogQuery(db).ExecuteAsync(criteria)).Id);
        criteria.PropertiesContain = "[]";
        await Assert.ThrowsExactlyAsync<FormatException>(() => new LogQuery(db).ExecuteAsync(criteria));
    }

    [TestMethod]
    public async Task EFCoreQuery_FiltersCommandsAndJoinedLogs()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "first");
        var second = Entry(2, date.AddHours(2), "second");
        first.Properties["TraceId"] = "abc";
        second.Properties["TraceId"] = "other";
        first.EFCoreEntry = new EFCoreEntry { TargetTables = ["Users", "Orders"], Tags = ["report"], CommandText = "select users", ElapsedMs = 100 };
        second.EFCoreEntry = new EFCoreEntry { TargetTables = ["Users"], Tags = ["report"], CommandText = "select users", ElapsedMs = 200 };
        db.LogEntries.AddRange(first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var query = new LogQuery(db);
        var commandCriteria = new LogQuery.EFCoreCriteria { Table = "Users", Tag = "report", CommandTextContains = "select", MinElapsedMs = 100 };
        var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date), TimeExpression = "asc" };
        var rows = await query.ExecuteAsync(commandCriteria, criteria);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, rows.Select(row => row.LogEntryId).ToArray());
        Assert.AreEqual("first", rows[0].LogEntry.Message);
        criteria.PropertiesContain = """{"TraceId":"abc"}""";
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(commandCriteria, criteria)).LogEntryId);
        commandCriteria.MinElapsedMs = 101;
        Assert.IsEmpty(await query.ExecuteAsync(commandCriteria, criteria));
        commandCriteria.Table = "Missing";
        Assert.IsEmpty(await query.ExecuteAsync(commandCriteria));
    }

    [TestMethod]
    public void ParseTimeExpression_FiltersRangesAndOrders()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            Entry(1, now.AddMinutes(-10), ""),
            Entry(2, now.AddMinutes(-90), ""),
            Entry(3, now.AddMinutes(-150), ""),
            Entry(4, now.AddMinutes(10), "")
        }.AsQueryable();
        CollectionAssert.AreEqual(new long[] { 1 }, LogQuery.ParseTimeExpression(entries, "30m").Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2 }, LogQuery.ParseTimeExpression(entries, "1:2h:asc").Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 3, 2, 1, 4 }, LogQuery.ParseTimeExpression(entries, "asc").Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 4, 1, 2, 3 }, LogQuery.ParseTimeExpression(entries, "").Select(row => row.Id).ToArray());
        foreach (var invalid in new[] { "2:1h", "-1h", "1x", ":asc", "1:asc:asc", "999999999999999999999999999d" })
            Assert.ThrowsExactly<FormatException>(() => LogQuery.ParseTimeExpression(entries, invalid));
    }

    [TestMethod]
    public async Task LogQuery_BasicContextAndCancellation()
    {
        await using var db = new BasicContext(new DbContextOptionsBuilder<BasicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        db.LogEntries.Add(Entry(1, date, "first"));
        await db.SaveChangesAsync();
        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date) };
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria)).Id);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => query.ExecuteAsync(new LogQuery.EFCoreCriteria()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(criteria, cancellation.Token));
    }

    private static LogEntry Entry(long id, DateTime timestamp, string message) => new()
    {
        Id = id, Timestamp = timestamp, Category = "Example", Level = LogLevel.Warning,
        Template = "template", Message = message
    };

    private static TestDbContext CreateContext() => new(new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

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
            modelBuilder.Entity<EFCoreEntry>().HasOne(entry => entry.LogEntry).WithOne(entry => entry.EFCoreEntry)
                .HasForeignKey<EFCoreEntry>(entry => entry.LogEntryId);
        }

    }

    private sealed class BasicContext(DbContextOptions<BasicContext> options) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => JsonSerializer.Serialize(properties, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, (JsonSerializerOptions?)null)!);
    }
}
