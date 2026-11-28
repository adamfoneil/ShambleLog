using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Queries;
using ShambleLog.RCL;

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
    public async Task Categories_RenderEffectiveParentsCountsAndLevelOptions()
    {
        await using var services = CreateServices();
        var levels = new CategoryLevelQuery(services.GetRequiredService<IServiceScopeFactory>());
        await levels.SaveAsync(new CategoryLevel { Category = "Default", Level = LogLevel.Information });
        await levels.SaveAsync(new CategoryLevel { Category = "Inventory", Level = LogLevel.Warning });
        await levels.SaveAsync(new CategoryLevel { Category = "Inventory.Queries", Level = LogLevel.Debug });
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        context.LogEntries.AddRange(
            new LogEntry { Category = "Other", Level = LogLevel.Information, Message = "other" },
            new LogEntry { Category = "Inventory.Processes", Level = LogLevel.Warning, Message = "process" },
            new LogEntry { Category = "inventory.queries.Read", Level = LogLevel.Debug, Message = "query" });
        await context.SaveChangesAsync();
        var queries = new ScopedQueryRunner(services.GetRequiredService<IServiceScopeFactory>());
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Categories>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Categories.CategoryLevelQuery)] = levels,
                [nameof(Categories.Queries)] = queries
            }));
            return rendered.ToHtmlString();
        });
        Assert.IsTrue(html.IndexOf("Default", StringComparison.Ordinal) < html.IndexOf("Other", StringComparison.Ordinal));
        Assert.IsTrue(html.IndexOf("Other", StringComparison.Ordinal) < html.IndexOf("<strong>Inventory</strong>", StringComparison.Ordinal));
        Assert.IsTrue(html.IndexOf("Inventory.Processes", StringComparison.Ordinal) < html.IndexOf("<strong>Inventory.Queries</strong>", StringComparison.Ordinal));
        StringAssert.Contains(html, "bg-warning text-dark");
        StringAssert.Contains(html, "title=\"Warning\">1</span>");
        StringAssert.Contains(html, "title=\"Increase to Information\">Info</button>");
        StringAssert.Contains(html, "title=\"Decrease to Information\">Info</button>");
        StringAssert.Contains(html, "title=\"Increase to Debug\">Debug</button>");
        StringAssert.Contains(html, "title=\"Decrease to Warning\">Warning</button>");
    }

    [TestMethod]
    [DataRow(LogLevel.Trace, "")]
    [DataRow(LogLevel.Debug, "Info")]
    [DataRow(LogLevel.Information, "Debug,Warning")]
    [DataRow(LogLevel.Warning, "Info")]
    [DataRow(LogLevel.Error, "")]
    [DataRow(LogLevel.Critical, "")]
    [DataRow(LogLevel.None, "")]
    public async Task Categories_OnlyRenderRequestedLiveLevelOptions(LogLevel level, string expected)
    {
        await using var services = CreateServices();
        var levels = new CategoryLevelQuery(services.GetRequiredService<IServiceScopeFactory>());
        await levels.SaveAsync(new CategoryLevel { Category = "Default", Level = level });
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        context.LogEntries.Add(new LogEntry { Category = "Observed", Level = LogLevel.Warning, Message = "test" });
        await context.SaveChangesAsync();
        var queries = new ScopedQueryRunner(services.GetRequiredService<IServiceScopeFactory>());
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Categories>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Categories.CategoryLevelQuery)] = levels,
                [nameof(Categories.Queries)] = queries
            }));
            return rendered.ToHtmlString();
        });
        var options = Regex.Matches(html, "title=\"(?:Increase|Decrease) to [^\"]+\">([^<]+)</button>")
            .Select(match => match.Groups[1].Value).ToArray();
        CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<string>() : expected.Split(','), options);
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
        services.AddLogging();
        services.AddDbContext<TestDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ILoggingDbContext>(provider => provider.GetRequiredService<TestDbContext>());
        services.AddTransient<CategorySummaryQuery>();
        return services.BuildServiceProvider();
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => JsonSerializer.Serialize(properties, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, (JsonSerializerOptions?)null)!);
    }
}
