using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;
using LogQuery = ShambleLog.Core.Queries.ClientSideLogQuery;

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
        foreach (var invalid in new[] { "2:1h", "-1h", "1x", ":asc", "1:asc:asc", "999999999999999999999999999d",
                     "930", "09300", "2400", "0960", "0930:2400", "1330:0930", "0930:930", "0930:1330:asc:asc" })
            Assert.ThrowsExactly<FormatException>(() => LogQuery.ParseTimeExpression(entries, invalid), invalid);
    }

    [TestMethod]
    public async Task LogQuery_ClockTimesUseSelectedDateAndInclusiveBounds()
    {
        await using var db = CreateContext();
        var date = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var before = Entry(1, date.AddHours(9).AddMinutes(29).AddSeconds(59), "before");
        var start = Entry(2, date.AddHours(9).AddMinutes(30), "start");
        var middle = Entry(3, date.AddHours(12), "middle");
        var end = Entry(4, date.AddHours(13).AddMinutes(30), "end");
        var after = Entry(5, date.AddHours(13).AddMinutes(30).AddTicks(1), "after");
        var nextDay = Entry(6, date.AddDays(1).AddHours(10), "next day");
        foreach (var entry in new[] { before, start, middle, end, after, nextDay })
            entry.EFCoreEntry = new EFCoreEntry { CommandText = entry.Message };
        db.LogEntries.AddRange(before, start, middle, end, after, nextDay);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date), TimeExpression = "0930:1330" };
        CollectionAssert.AreEqual(new long[] { 4, 3, 2 }, (await query.ExecuteAsync(criteria)).Select(row => row.Id).ToArray());
        criteria.TimeExpression = "0930:1330:ASC";
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, (await query.ExecuteAsync(criteria)).Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, (await query.ExecuteAsync(new LogQuery.EFCoreCriteria(), criteria)).Select(row => row.LogEntryId).ToArray());
        criteria.TimeExpression = "0930";
        CollectionAssert.AreEqual(new long[] { 5, 4, 3, 2 }, (await query.ExecuteAsync(criteria)).Select(row => row.Id).ToArray());
        criteria.TimeExpression = "0930:0930";
        CollectionAssert.AreEqual(new long[] { 2 }, (await query.ExecuteAsync(criteria)).Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public void ParseTimeExpression_ClockTimesDefaultToTodayAndStopAtNow()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var entries = new[]
        {
            Entry(1, today.AddDays(-1), "yesterday"),
            Entry(2, today, "midnight"),
            Entry(3, now.AddDays(1), "future")
        }.AsQueryable();
        CollectionAssert.AreEqual(new long[] { 2 }, LogQuery.ParseTimeExpression(entries, "0000").Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2 }, LogQuery.ParseTimeExpression(entries, "0000:2359:asc").Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public async Task SummaryQueries_ClockTimesUseCurrentUtcDate()
    {
        await using var db = CreateContext();
        var today = DateTime.UtcNow.Date;
        var current = Entry(1, today, "current");
        var previous = Entry(2, today.AddDays(-1), "previous");
        current.EFCoreEntry = new EFCoreEntry { ElapsedMs = 100 };
        previous.EFCoreEntry = new EFCoreEntry { ElapsedMs = 200 };
        db.LogEntries.AddRange(current, previous);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var category = Assert.ContainsSingle(await new CategorySummaryQuery(db).ExecuteAsync("0000:2359"));
        Assert.AreEqual(1, category.LevelCounts[LogLevel.Warning]);
        var commands = Assert.ContainsSingle(await new EFCoreSummaryQuery(db).ExecuteAsync("0000:2359"));
        Assert.AreEqual(1, commands.TotalCalls);
        Assert.AreEqual(100d, commands.AvgElapsedMs);
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

    [TestMethod]
    public async Task CategorySummaryQuery_CountsCategoriesAndLevels()
    {
        await using var db = new BasicContext(new DbContextOptionsBuilder<BasicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var query = new CategorySummaryQuery(db);
        Assert.IsEmpty(await query.ExecuteAsync());

        var now = DateTime.UtcNow;
        db.LogEntries.AddRange(
            Entry(1, now.AddMinutes(-10), "first"),
            Entry(2, now.AddMinutes(-90), "second"),
            Entry(3, now.AddMinutes(-150), "third"),
            new LogEntry { Id = 4, Timestamp = now.AddMinutes(-10), Category = "Example", Level = LogLevel.Error },
            new LogEntry { Id = 5, Timestamp = now.AddMinutes(-10), Category = "Other", Level = LogLevel.Warning },
            new LogEntry { Id = 6, Timestamp = now.AddMinutes(10), Category = "Future", Level = LogLevel.Information });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var all = (await query.ExecuteAsync()).ToDictionary(result => result.Category);
        Assert.AreEqual(3, all.Count);
        Assert.AreEqual(3, all["Example"].LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, all["Example"].LevelCounts[LogLevel.Error]);
        Assert.AreEqual(2, all["Example"].LevelCounts.Count);
        Assert.AreEqual(1, all["Other"].LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, all["Future"].LevelCounts[LogLevel.Information]);
        Assert.AreEqual(3, (await query.ExecuteAsync("asc")).Length);

        var recent = (await query.ExecuteAsync("30m")).ToDictionary(result => result.Category);
        Assert.AreEqual(2, recent.Count);
        Assert.AreEqual(1, recent["Example"].LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, recent["Example"].LevelCounts[LogLevel.Error]);
        Assert.AreEqual(1, recent["Other"].LevelCounts[LogLevel.Warning]);

        var range = Assert.ContainsSingle(await query.ExecuteAsync("1:2h:asc"));
        Assert.AreEqual("Example", range.Category);
        Assert.AreEqual(1, range.LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, range.LevelCounts.Count);
        await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync("2:1h"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task EFCoreSummaryQuery_AggregatesCommandsAndFiltersByTimeExpression()
    {
        await using var db = CreateContext();
        var now = DateTime.UtcNow;
        var first = Entry(1, now.AddMinutes(-10), "first");
        first.EFCoreEntry = new EFCoreEntry
        {
            TargetTables = ["Users", " Orders "],
            Tags = ["report"],
            ElapsedMs = 100
        };
        var second = Entry(2, now.AddMinutes(-5), "second");
        second.EFCoreEntry = new EFCoreEntry
        {
            TargetTables = ["orders", "users", "USERS"],
            Tags = ["REPORT"],
            ElapsedMs = 200
        };
        var older = Entry(3, now.AddMinutes(-90), "older");
        older.EFCoreEntry = new EFCoreEntry { ElapsedMs = 300 };
        var future = Entry(4, now.AddMinutes(10), "future");
        future.EFCoreEntry = new EFCoreEntry { TargetTables = ["Future"], ElapsedMs = 400 };
        db.LogEntries.AddRange(first, second, older, future);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new EFCoreSummaryQuery(db);
        var all = await query.ExecuteAsync();
        Assert.AreEqual(3, all.Length);
        Assert.AreEqual(2, all[0].TotalCalls);
        Assert.AreEqual(150d, all[0].AvgElapsedMs);
        CollectionAssert.AreEqual(new[] { "Orders", "Users" }, all[0].FromTables);
        CollectionAssert.AreEqual(new[] { "report" }, all[0].QueryTags);
        Assert.AreEqual(100L, all[0].MinElapsedMs);
        Assert.AreEqual(200L, all[0].MaxElapsedMs);
        Assert.AreEqual(now.AddMinutes(-5), all[0].LatestTimestamp);
        CollectionAssert.AreEqual(new[] { "Future" }, all[1].FromTables);
        Assert.AreEqual(400d, all[1].AvgElapsedMs);

        var recent = Assert.ContainsSingle(await query.ExecuteAsync("30m"));
        Assert.AreEqual(2, recent.TotalCalls);
        Assert.AreEqual(150d, recent.AvgElapsedMs);
        await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync("2:1h"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task PropertyFilterHook_AppliesServerAndClientFiltersToBothQueries()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "keep");
        var second = Entry(2, date.AddHours(2), "keep");
        var third = Entry(3, date.AddHours(3), "skip");
        first.EFCoreEntry = new EFCoreEntry { CommandText = "first" };
        second.EFCoreEntry = new EFCoreEntry { CommandText = "second" };
        third.EFCoreEntry = new EFCoreEntry { CommandText = "third" };
        db.LogEntries.AddRange(first, second, third);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new HookLogQuery(db);
        var criteria = new ShambleLog.Core.Queries.LogQuery.Criteria
        {
            Date = DateOnly.FromDateTime(date),
            PropertiesContain = """{"Limit":2}"""
        };
        CollectionAssert.AreEqual(new long[] { 1 }, (await query.ExecuteAsync(criteria)).Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 1 }, (await query.ExecuteAsync(new ShambleLog.Core.Queries.LogQuery.EFCoreCriteria(), criteria))
            .Select(row => row.LogEntryId).ToArray());
    }

    private sealed class HookLogQuery(ILoggingDbContext db) : ShambleLog.Core.Queries.LogQuery(db)
    {
        protected override (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
            IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties)
        {
            var limit = properties.Single(property => property.Key == "Limit").Value.GetInt64();
            return (query.Where(entry => entry.Message == "keep"), entry => entry.Id < limit);
        }
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
