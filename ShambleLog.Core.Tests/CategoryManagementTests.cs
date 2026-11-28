using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class CategoryManagementTests
{
    [TestMethod]
    public async Task CategoryLevels_CreateEditRenameDeleteAndValidate()
    {
        await using var services = CreateServices();
        var query = new CategoryLevelQuery(services.GetRequiredService<IServiceScopeFactory>());
        await query.SaveAsync(new CategoryLevel { Category = "Default", Level = LogLevel.Information });
        await query.SaveAsync(new CategoryLevel { Category = "Inventory", Level = LogLevel.Warning });
        await query.SaveAsync(new CategoryLevel { Category = "Inventory.Queries", Level = LogLevel.Debug, IsTemporary = true });
        await query.SaveAsync(new CategoryLevel { Category = "Inventory.Queries", Level = LogLevel.Information, IsTemporary = true }, "Inventory.Queries");
        Assert.IsTrue((await query.ExecuteAsync()).Single(item => item.Category == "Inventory.Queries").IsTemporary);
        await query.SaveAsync(new CategoryLevel { Category = "Inventory.Processes", Level = LogLevel.Error }, "Inventory");
        Assert.IsFalse((await query.ExecuteAsync()).Any(item => item.Category == "Inventory"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            query.SaveAsync(new CategoryLevel { Category = "inventory.queries", Level = LogLevel.Warning }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => query.DeleteAsync("Default"));
        await query.DeleteAsync("Inventory.Queries");
        CollectionAssert.AreEquivalent(new[] { "Default", "Inventory.Processes" }, (await query.ExecuteAsync()).Select(item => item.Category).ToArray());
    }

    [TestMethod]
    public void CategorySummaryProjection_GroupsCategoriesAndPreservesFullNames()
    {
        var rules = new[]
        {
            new CategoryLevel { Category = "Default", Level = LogLevel.Information },
            new CategoryLevel { Category = "Inventory", Level = LogLevel.Warning },
            new CategoryLevel { Category = "Inventory.Queries", Level = LogLevel.Debug }
        };
        var summaries = new[]
        {
            new CategorySummaryQuery.Result { Category = "Default.Other", LevelCounts = new() { [LogLevel.Error] = 4 } },
            new CategorySummaryQuery.Result { Category = "Inventory", LevelCounts = new() { [LogLevel.Warning] = 1 } },
            new CategorySummaryQuery.Result { Category = "Inventory.Processes", LevelCounts = new() { [LogLevel.Warning] = 2 } },
            new CategorySummaryQuery.Result { Category = "InventoryExtra", LevelCounts = new() { [LogLevel.Information] = 1 } },
            new CategorySummaryQuery.Result { Category = "Other", LevelCounts = new() { [LogLevel.Information] = 1 } },
            new CategorySummaryQuery.Result { Category = "inventory.Queries", LevelCounts = new() { [LogLevel.Warning] = 2 } },
            new CategorySummaryQuery.Result { Category = "inventory.queries.Read", LevelCounts = new() { [LogLevel.Debug] = 3 } }
        };

        var projection = CategorySummaryProjection.Build(rules, summaries);
        CollectionAssert.AreEquivalent(
            new[] { "Default.Other", "Other" },
            projection.Single(group => group.CategoryLevel.Category == "Default").Summaries.Select(item => item.Result.Category).ToArray());

        var inventory = projection.Single(group => group.CategoryLevel.Category == "Inventory").Summaries;
        var process = inventory.Single(item => item.Result.Category == "Inventory.Processes");
        Assert.AreEqual("Processes", process.DisplayCategory);
        Assert.AreEqual(2, process.Result.LevelCounts[LogLevel.Warning]);
        var prefix = inventory.Single(item => item.Result.Category == "InventoryExtra");
        Assert.AreEqual("InventoryExtra", prefix.Result.Category);
        Assert.AreEqual("Extra", prefix.DisplayCategory);

        var queryGroup = projection.Single(group => group.CategoryLevel.Category == "Inventory.Queries");
        var querySummary = queryGroup.Summaries.Single(item => item.Result.Category == "inventory.queries.Read");
        Assert.AreEqual("Read", querySummary.DisplayCategory);
        Assert.AreEqual(3, querySummary.Result.LevelCounts[LogLevel.Debug]);
    }

    [TestMethod]
    [DataRow(LogLevel.Trace, "")]
    [DataRow(LogLevel.Debug, "Information")]
    [DataRow(LogLevel.Information, "Debug,Warning")]
    [DataRow(LogLevel.Warning, "Information")]
    [DataRow(LogLevel.Error, "")]
    [DataRow(LogLevel.Critical, "")]
    [DataRow(LogLevel.None, "")]
    public void CategorySummaryProjection_ProvidesOnlyAdjacentLiveLevelOptions(LogLevel level, string expected)
    {
        var rule = new CategoryLevel { Category = "Default", Level = level };
        var summary = new CategorySummaryQuery.Result { Category = "Observed" };

        var options = CategorySummaryProjection.Build([rule], [summary]).Single().Summaries.Single().LiveLevelOptions;
        var expectedLevels = expected.Length == 0
            ? Array.Empty<LogLevel>()
            : expected.Split(',').Select(Enum.Parse<LogLevel>).ToArray();
        CollectionAssert.AreEqual(expectedLevels, options);
    }

    [TestMethod]
    public async Task LiveCategoryLevel_CreatesTemporaryRuleAndPreservesExistingRules()
    {
        await using var services = CreateServices();
        var query = new CategoryLevelQuery(services.GetRequiredService<IServiceScopeFactory>());
        await query.SaveAsync(new CategoryLevel { Category = "Inventory", Level = LogLevel.Warning });
        await query.SetCategoryLevelAsync("Inventory.Queries", LogLevel.Debug);
        var rules = await query.ExecuteAsync();
        Assert.AreEqual(LogLevel.Warning, rules.Single(item => item.Category == "Inventory").Level);
        Assert.IsTrue(rules.Single(item => item.Category == "Inventory.Queries").IsTemporary);
        await query.SetCategoryLevelAsync("inventory.queries", LogLevel.Information);
        await query.SetCategoryLevelAsync("inventory", LogLevel.Error);
        rules = await query.ExecuteAsync();
        Assert.AreEqual(LogLevel.Information, rules.Single(item => item.Category == "Inventory.Queries").Level);
        Assert.IsTrue(rules.Single(item => item.Category == "Inventory.Queries").IsTemporary);
        Assert.AreEqual(LogLevel.Error, rules.Single(item => item.Category == "Inventory").Level);
        Assert.IsFalse(rules.Single(item => item.Category == "Inventory").IsTemporary);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<TestDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ILoggingDbContext>(provider => provider.GetRequiredService<TestDbContext>());
        return services.BuildServiceProvider();
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
