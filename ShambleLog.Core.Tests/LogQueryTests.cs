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
        var rows = await query.ExecuteAsync(criteria, 0, int.MaxValue);
        CollectionAssert.AreEqual(new long[] { 2, 1 }, rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("select", rows[0].EFCoreEntry?.CommandText);
        Assert.IsNull(rows[1].EFCoreEntry);

        criteria.TimeExpression = "asc";
        criteria.CategoryContains = "Example";
        criteria.Level = LogLevel.Warning;
        criteria.TemplateContains = "template";
        criteria.MessageContains = "first";
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 0, int.MaxValue)).Id);
        criteria.CategoryContains = "Other";
        Assert.IsEmpty(await query.ExecuteAsync(criteria, 0, int.MaxValue));
    }

    [TestMethod]
    public async Task LogQuery_DateIsOptional()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        db.LogEntries.AddRange(Entry(1, date, "first"), Entry(2, date.AddDays(1), "second"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria();
        CollectionAssert.AreEqual(new long[] { 2, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());

        criteria.Date = DateOnly.FromDateTime(date);
        CollectionAssert.AreEqual(new long[] { 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public async Task CategoryContains_FiltersBothQueryTypesBeforePagination()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "first");
        first.Category = "App.Example.Service";
        var second = Entry(2, date.AddHours(2), "second");
        second.Category = "Other.EXAMPLE.Worker";
        var third = Entry(3, date.AddHours(3), "third");
        third.Category = "Unrelated";
        foreach (var entry in new[] { first, second, third })
            entry.EFCoreEntry = new EFCoreEntry { CommandText = entry.Message };
        db.LogEntries.AddRange(first, second, third);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date), CategoryContains = "eXaMpLe" };
        CollectionAssert.AreEqual(new long[] { 2, 1 }, (await query.ExecuteAsync(criteria, 0, 2)).Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2, 1 }, (await query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, 2, criteria))
            .Select(row => row.LogEntryId).ToArray());
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 1, 1)).Id);

        criteria.CategoryContains = "missing";
        Assert.IsEmpty(await query.ExecuteAsync(criteria, 0, int.MaxValue));
        criteria.CategoryContains = "  ";
        CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public async Task CategoryAndLevel_FiltersBothQueryTypesBeforePagination()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var match = Entry(1, date, "match");
        match.Category = "Inventory.Queries";
        var otherLevel = Entry(2, date.AddMinutes(1), "other level");
        otherLevel.Category = "Inventory.Queries";
        otherLevel.Level = LogLevel.Error;
        var otherCategory = Entry(3, date.AddMinutes(2), "other category");
        otherCategory.Category = "Other";
        foreach (var entry in new[] { match, otherLevel, otherCategory })
            entry.EFCoreEntry = new EFCoreEntry { CommandText = entry.Message };
        db.LogEntries.AddRange(match, otherLevel, otherCategory);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { CategoryContains = "Inventory.Queries", Level = LogLevel.Warning };
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 0, 1)).Id);
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, 1, criteria)).LogEntryId);
        criteria.Level = LogLevel.Error;
        Assert.AreEqual(2L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 0, 1)).Id);
    }

    [TestMethod]
    public async Task ContainsCriteria_ExcludeMatchesBeforePaginationInBothQueryTypes()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "match message");
        first.Category = "App.Match";
        first.Template = "match template";
        var second = Entry(2, date.AddHours(2), "other message");
        second.Category = "Other";
        second.Template = "other template";
        var third = Entry(3, date.AddHours(3), "match message");
        third.Category = "MATCH.Service";
        third.Template = "match template";
        foreach (var entry in new[] { first, second, third })
            entry.EFCoreEntry = new EFCoreEntry { CommandText = entry.Message };
        db.LogEntries.AddRange(first, second, third);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new LogQuery(db);
        Action<LogQuery.Criteria, string?>[] setters =
        [
            (criteria, value) => criteria.CategoryContains = value,
            (criteria, value) => criteria.TemplateContains = value,
            (criteria, value) => criteria.MessageContains = value
        ];
        foreach (var set in setters)
        {
            var criteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date) };
            foreach (var prefix in new[] { "!", "-" })
            {
                set(criteria, prefix + "match");
                CollectionAssert.AreEqual(new long[] { 2 }, (await query.ExecuteAsync(criteria, 0, 1)).Select(row => row.Id).ToArray());
                CollectionAssert.AreEqual(new long[] { 2 }, (await query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, 1, criteria))
                    .Select(row => row.LogEntryId).ToArray());
                set(criteria, prefix + "missing");
                CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
                set(criteria, prefix);
                Assert.IsEmpty(await query.ExecuteAsync(criteria, 0, int.MaxValue));
            }
            set(criteria, "match");
            CollectionAssert.AreEqual(new long[] { 3, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
            set(criteria, "-!match");
            CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
            set(criteria, "  ");
            CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        }

        var caseCriteria = new LogQuery.Criteria { Date = DateOnly.FromDateTime(date), CategoryContains = "!mAtCh" };
        Assert.AreEqual(2L, Assert.ContainsSingle(await query.ExecuteAsync(caseCriteria, 0, int.MaxValue)).Id);
        caseCriteria.CategoryContains = null;
        caseCriteria.TemplateContains = "!MATCH";
        CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(caseCriteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        caseCriteria.TemplateContains = null;
        caseCriteria.MessageContains = "!MATCH";
        CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, (await query.ExecuteAsync(caseCriteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());

        var combined = new LogQuery.Criteria
        {
            Date = DateOnly.FromDateTime(date), CategoryContains = "!mAtCh", TemplateContains = "other", MessageContains = "-match"
        };
        Assert.AreEqual(2L, Assert.ContainsSingle(await query.ExecuteAsync(combined, 0, int.MaxValue)).Id);
    }

    [TestMethod]
    public async Task LogQuery_PropertiesMatchShorthandStringValuesAndExclusions()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date, "first");
        first.Properties = new() { ["UserId"] = 123, ["TraceId"] = "abc", ["Method"] = "Get" };
        var second = Entry(2, date.AddHours(1), "second");
        second.Properties = new() { ["UserId"] = "123", ["TraceId"] = "abc", ["Method"] = "Post", ["Message"] = "hello, \"world\": \\path" };
        var missing = Entry(3, date.AddHours(2), "missing");
        missing.Properties = new() { ["UserId"] = "123", ["TraceId"] = "abc" };
        db.LogEntries.AddRange(first, second, missing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria
        {
            Date = DateOnly.FromDateTime(date),
            PropertiesContain = " UserId:123, TraceId:abc, Method:-Get "
        };
        CollectionAssert.AreEqual(new long[] { 3, 2 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        criteria.PropertiesContain = "Message:\"hello, \\\"world\\\": \\\\path\", UserId:123";
        Assert.AreEqual(2L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 0, int.MaxValue)).Id);
        criteria.PropertiesContain = "UserId:123";
        CollectionAssert.AreEqual(new long[] { 3, 2 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        criteria.PropertiesContain = "  ";
        Assert.AreEqual(3, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Length);
    }

    [TestMethod]
    public async Task LogQuery_RejectsMalformedPropertyFiltersInBothQueryTypes()
    {
        await using var db = CreateContext();
        var query = new LogQuery(db);
        foreach (var expression in new[] { "UserId", ":123", "UserId:", "UserId:123,", "UserId:123,,Method:Get", "UserId:\"unterminated", "UserId:\"bad\\q\"", "UserId:\"ok\"extra", """{"UserId":"123"}""" })
        {
            var criteria = new LogQuery.Criteria { PropertiesContain = expression };
            await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync(criteria, 0, 10), expression);
            await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, 10, criteria), expression);
        }
        var valid = new LogQuery.Criteria { PropertiesContain = "Empty:\"\", Value:\"-x\", Path:\"C:\\\\test\"" };
        Assert.IsEmpty(await query.ExecuteAsync(valid, 0, 10));
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
        var rows = await query.ExecuteAsync(commandCriteria, 0, int.MaxValue, criteria);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, rows.Select(row => row.LogEntryId).ToArray());
        Assert.AreEqual("first", rows[0].LogEntry.Message);
        criteria.PropertiesContain = "TraceId:abc";
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(commandCriteria, 0, int.MaxValue, criteria)).LogEntryId);
        commandCriteria.MinElapsedMs = 101;
        Assert.IsEmpty(await query.ExecuteAsync(commandCriteria, 0, int.MaxValue, criteria));
        commandCriteria.Table = "Missing";
        Assert.IsEmpty(await query.ExecuteAsync(commandCriteria, 0, int.MaxValue));
    }

    [TestMethod]
    public async Task EFCoreQuery_MatchesCaseInsensitiveSubstringsBeforePagination()
    {
        await using var db = CreateContext();
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = Entry(1, date.AddHours(1), "first");
        var second = Entry(2, date.AddHours(2), "second");
        var third = Entry(3, date.AddHours(3), "third");
        first.EFCoreEntry = new EFCoreEntry { TargetTables = ["Orders", "UserProfiles"], Tags = ["other", "MonthlyReport"], CommandText = "SELECT * FROM UserProfiles" };
        second.EFCoreEntry = new EFCoreEntry { TargetTables = ["USERPROFILES"], Tags = ["MONTHLYREPORT"], CommandText = "select * from userprofiles" };
        third.EFCoreEntry = new EFCoreEntry { TargetTables = ["User", "Profiles", "A%_Table"], Tags = ["Monthly", "Report", "A%_Tag"], CommandText = "SELECT * FROM UserProfiles" };
        db.LogEntries.AddRange(first, second, third);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var query = new LogQuery(db);
        var criteria = new LogQuery.Criteria { TimeExpression = "asc" };

        foreach (var filter in new[]
        {
            new LogQuery.EFCoreCriteria { Table = "rPrO" },
            new LogQuery.EFCoreCriteria { Tag = "lyRe" },
            new LogQuery.EFCoreCriteria { CommandTextContains = "FrOm" },
            new LogQuery.EFCoreCriteria { Table = "rPrO", Tag = "lyRe", CommandTextContains = "FrOm" }
        })
        {
            var expected = filter.Table is null && filter.Tag is null ? new long[] { 1, 2, 3 } : [1, 2];
            CollectionAssert.AreEqual(expected, (await query.ExecuteAsync(filter, 0, int.MaxValue, criteria)).Select(row => row.LogEntryId).ToArray());
        }

        var combined = new LogQuery.EFCoreCriteria { Table = "rPrO", Tag = "lyRe", CommandTextContains = "FrOm" };
        CollectionAssert.AreEqual(new long[] { 2 }, (await query.ExecuteAsync(combined, 1, 1, criteria)).Select(row => row.LogEntryId).ToArray());
        combined.Table = "missing";
        Assert.IsEmpty(await query.ExecuteAsync(combined, 0, int.MaxValue));
        Assert.IsEmpty(await query.ExecuteAsync(new LogQuery.EFCoreCriteria { Tag = "missing" }, 0, int.MaxValue));
        Assert.IsEmpty(await query.ExecuteAsync(new LogQuery.EFCoreCriteria { CommandTextContains = "missing" }, 0, int.MaxValue));
        CollectionAssert.AreEqual(new long[] { 3 }, (await query.ExecuteAsync(new LogQuery.EFCoreCriteria { Table = "%_", Tag = "%_" }, 0, int.MaxValue)).Select(row => row.LogEntryId).ToArray());
        combined.Table = " ";
        combined.Tag = "";
        combined.CommandTextContains = null;
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, (await query.ExecuteAsync(combined, 0, int.MaxValue, criteria)).Select(row => row.LogEntryId).ToArray());
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
        CollectionAssert.AreEqual(new long[] { 4, 3, 2 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        criteria.TimeExpression = "0930:1330:ASC";
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, (await query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, int.MaxValue, criteria)).Select(row => row.LogEntryId).ToArray());
        criteria.TimeExpression = "0930";
        CollectionAssert.AreEqual(new long[] { 5, 4, 3, 2 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        criteria.TimeExpression = "0930:0930";
        CollectionAssert.AreEqual(new long[] { 2 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
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
        current.EFCoreEntry = new EFCoreEntry { Tags = ["tag"], ElapsedMs = 100 };
        previous.EFCoreEntry = new EFCoreEntry { Tags = ["tag"], ElapsedMs = 200 };
        db.LogEntries.AddRange(current, previous);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var category = Assert.ContainsSingle(await new CategorySummaryQuery(db).ExecuteAsync(timeExpression: "0000:2359"));
        Assert.AreEqual(1, category.LevelCounts[LogLevel.Warning]);
        var commands = Assert.ContainsSingle(await new EFCoreMetricsQuery(db).ExecuteAsync(null, "0000:2359", default));
        Assert.AreEqual(1, commands.Count);
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
        Assert.AreEqual(1L, Assert.ContainsSingle(await query.ExecuteAsync(criteria, 0, int.MaxValue)).Id);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => query.ExecuteAsync(new LogQuery.EFCoreCriteria(), 0, int.MaxValue));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(criteria, 0, int.MaxValue, cancellation.Token));
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
        Assert.AreEqual(3, (await query.ExecuteAsync(timeExpression: "asc")).Length);

        var recent = (await query.ExecuteAsync(timeExpression: "30m")).ToDictionary(result => result.Category);
        Assert.AreEqual(2, recent.Count);
        Assert.AreEqual(1, recent["Example"].LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, recent["Example"].LevelCounts[LogLevel.Error]);
        Assert.AreEqual(1, recent["Other"].LevelCounts[LogLevel.Warning]);

        var range = Assert.ContainsSingle(await query.ExecuteAsync(timeExpression: "1:2h:asc"));
        Assert.AreEqual("Example", range.Category);
        Assert.AreEqual(1, range.LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(1, range.LevelCounts.Count);
        await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync(timeExpression: "2:1h"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task CategorySummaryQuery_FiltersUtcDateAndAnchorsClockExpressions()
    {
        await using var db = CreateContext();
        var day = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        db.LogEntries.AddRange(
            Entry(1, day.AddTicks(-1), "previous"),
            Entry(2, day, "midnight"),
            Entry(3, day.AddHours(9).AddMinutes(30), "start"),
            Entry(4, day.AddHours(13).AddMinutes(30), "end"),
            Entry(5, day.AddHours(13).AddMinutes(30).AddTicks(1), "after"),
            Entry(6, day.AddDays(1), "next"),
            Entry(7, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), "maximum"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new CategorySummaryQuery(db);
        var date = DateOnly.FromDateTime(day);
        Assert.AreEqual(7, Assert.ContainsSingle(await query.ExecuteAsync()).LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(4, Assert.ContainsSingle(await query.ExecuteAsync(date)).LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(2, Assert.ContainsSingle(await query.ExecuteAsync(date, "0930:1330")).LevelCounts[LogLevel.Warning]);
        Assert.AreEqual(3, Assert.ContainsSingle(await query.ExecuteAsync(date, "0930")).LevelCounts[LogLevel.Warning]);
        Assert.IsEmpty(await query.ExecuteAsync(date, "30m"));
        Assert.AreEqual(1, Assert.ContainsSingle(await query.ExecuteAsync(DateOnly.MaxValue)).LevelCounts[LogLevel.Warning]);
    }

    [TestMethod]
    public async Task EFCoreMetricsQuery_AggregatesEachDistinctTagAndTable()
    {
        await using var db = CreateContext();
        var day = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 20; i++)
        {
            var entry = Entry(i, day.AddHours(10), "command");
            entry.EFCoreEntry = new EFCoreEntry
            {
                Tags = i == 1 ? [" Report ", "REPORT", "", "Shared"] : ["report"],
                TargetTables = i == 1 ? [" Users ", "USERS", "Shared"] : ["users"],
                ElapsedMs = i
            };
            db.LogEntries.Add(entry);
        }
        var unnamed = Entry(21, day.AddHours(10), "unnamed");
        unnamed.EFCoreEntry = new EFCoreEntry { ElapsedMs = 1000 };
        db.LogEntries.Add(unnamed);
        db.LogEntries.Add(Entry(22, day.AddHours(10), "not a command"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new EFCoreMetricsQuery(db);
        var results = await query.ExecuteAsync(null, null, default);
        Assert.AreEqual(4, results.Length);
        foreach (var (type, name) in new[] { (EFCoreMetricsQueryType.Tag, "report"), (EFCoreMetricsQueryType.Table, "users") })
        {
            var metric = results.Single(result => result.Type == type && result.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(20, metric.Count);
            Assert.AreEqual(1L, metric.MinElapsedMs);
            Assert.AreEqual(20L, metric.MaxElapsedMs);
            Assert.AreEqual(10.5d, metric.AvgElapsedMs);
            Assert.AreEqual(19d, metric.P95ElapsedMs);
        }
        foreach (var metric in results.Where(result => result.Name == "Shared"))
        {
            Assert.AreEqual(1, metric.Count);
            Assert.AreEqual(1d, metric.P95ElapsedMs);
        }
        Assert.AreEqual(2, results.Count(result => result.Name == "Shared"));
    }

    [TestMethod]
    public async Task EFCoreMetricsQuery_FiltersDateAndTimeAndPropagatesErrors()
    {
        await using var db = CreateContext();
        var day = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var times = new[] { day.AddTicks(-1), day, day.AddHours(9).AddMinutes(30),
            day.AddHours(13).AddMinutes(30), day.AddHours(13).AddMinutes(30).AddTicks(1), day.AddDays(1),
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-90), DateTime.UtcNow.AddMinutes(10) };
        for (var i = 0; i < times.Length; i++)
        {
            var entry = Entry(i + 1, times[i], "command");
            entry.EFCoreEntry = new EFCoreEntry { Tags = ["tag"], ElapsedMs = i + 1 };
            db.LogEntries.Add(entry);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new EFCoreMetricsQuery(db);
        var date = DateOnly.FromDateTime(day);
        Assert.AreEqual(9, Assert.ContainsSingle(await query.ExecuteAsync(null, null, default)).Count);
        Assert.AreEqual(4, Assert.ContainsSingle(await query.ExecuteAsync(date, null, default)).Count);
        var range = Assert.ContainsSingle(await query.ExecuteAsync(date, "0930:1330:asc", default));
        Assert.AreEqual(2, range.Count);
        Assert.AreEqual(3L, range.MinElapsedMs);
        Assert.AreEqual(4L, range.MaxElapsedMs);
        Assert.AreEqual(4d, range.P95ElapsedMs);
        Assert.AreEqual(3, Assert.ContainsSingle(await query.ExecuteAsync(date, "0930", default)).Count);
        Assert.IsEmpty(await query.ExecuteAsync(date, "30m", default));
        Assert.AreEqual(1, Assert.ContainsSingle(await query.ExecuteAsync(null, "30m", default)).Count);
        Assert.AreEqual(1, Assert.ContainsSingle(await query.ExecuteAsync(null, "1:2h", default)).Count);
        await Assert.ThrowsExactlyAsync<FormatException>(() => query.ExecuteAsync(null, "2:1h", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.ExecuteAsync(null, null, cancellation.Token));
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
            PropertiesContain = "Limit:2"
        };
        CollectionAssert.AreEqual(new long[] { 1 }, (await query.ExecuteAsync(criteria, 0, int.MaxValue)).Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 1 }, (await query.ExecuteAsync(new ShambleLog.Core.Queries.LogQuery.EFCoreCriteria(), 0, int.MaxValue, criteria))
            .Select(row => row.LogEntryId).ToArray());
    }

    private sealed class HookLogQuery(ILoggingDbContext db) : ShambleLog.Core.Queries.LogQuery(db)
    {
        protected override (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
            IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties)
        {
            var limit = long.Parse(properties.Single(property => property.Key == "Limit").Value.GetString()!);
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
