using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class LoggingIntegrationTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_StructuredMessage_PersistsMessagePropertiesExceptionAndScopes()
    {
        using var host = CreateHost();
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();
        var exception = new InvalidOperationException("failure");

        using (logger.BeginScope("Scope {ScopeId}", 7))
        {
            logger.LogWarning(new EventId(12, "Test"), exception, "Hello {Name} {Count}", "world", 42);
        }
        await host.StopAsync(testContext.CancellationToken);

        var entry = Assert.ContainsSingle(await ReadEntriesAsync(host));
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        Assert.AreEqual(typeof(LoggingIntegrationTests).FullName, entry.Category);
        Assert.AreEqual("Hello {Name} {Count}", entry.Template);
        Assert.AreEqual("Hello world 42", entry.Message);
        Assert.AreEqual(exception.ToString(), entry.Exception);
        Assert.AreEqual(DateTimeKind.Utc, entry.Timestamp.Kind);
        Assert.HasCount(5, entry.Properties);
        Assert.AreEqual("world", Property(entry, "Name").GetString());
        Assert.AreEqual(42, Property(entry, "Count").GetInt32());
        Assert.AreEqual(12, Property(entry, "EventId").GetInt32());
        Assert.AreEqual("Test", Property(entry, "EventName").GetString());
        var scopes = Property(entry, "Scopes").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.AreEqual("Scope 7", Assert.ContainsSingle(scopes));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_BelowMinimumLevel_DoesNotPersistFilteredEntries()
    {
        using var host = CreateHost();
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();

        logger.LogInformation("Filtered {Value}", 1);
        logger.Log(LogLevel.None, "Disabled");
        logger.LogWarning("Retained");
        await host.StopAsync(testContext.CancellationToken);

        var entry = Assert.ContainsSingle(await ReadEntriesAsync(host));
        Assert.AreEqual("Retained", entry.Message);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_CategoryRules_UseMostSpecificPrefixAndDefault()
    {
        using var host = CreateHost();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            context.LogCategoryLevels.AddRange(
                new CategoryLevel { Category = "Default", Level = LogLevel.Error },
                new CategoryLevel { Category = "Example", Level = LogLevel.Warning },
                new CategoryLevel { Category = "Example.Detail", Level = LogLevel.Critical },
                new CategoryLevel { Category = "Example.Disabled", Level = LogLevel.None });
            await context.SaveRulesAsync(testContext.CancellationToken);
        }

        await host.StartAsync(testContext.CancellationToken);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Other").LogWarning("default rejected");
        factory.CreateLogger("Other").LogError("default accepted");
        factory.CreateLogger("example.Other").LogWarning("prefix accepted");
        factory.CreateLogger("Example.Detail.Child").LogError("specific rejected");
        factory.CreateLogger("Example.Detail.Child").LogCritical("specific accepted");
        factory.CreateLogger("Example.Disabled").LogCritical("disabled rejected");
        await host.StopAsync(testContext.CancellationToken);

        var entries = await ReadEntriesAsync(host);
        CollectionAssert.AreEqual(new[] { "default accepted", "prefix accepted", "specific accepted" },
            entries.Select(entry => entry.Message).ToArray());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_ChangedCategoryRule_AppliesToSubsequentEntries()
    {
        using var host = CreateHost();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            context.LogCategoryLevels.Add(new CategoryLevel { Category = "Default", Level = LogLevel.Warning });
            await context.SaveRulesAsync(testContext.CancellationToken);
        }

        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();
        logger.LogWarning("before change");
        await host.Services.GetRequiredService<WriteBehavior>().EntrySaved.WaitAsync(testContext.CancellationToken);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            var rule = await context.LogCategoryLevels.SingleAsync(testContext.CancellationToken);
            rule.Level = LogLevel.Error;
            await context.SaveRulesAsync(testContext.CancellationToken);
        }
        logger.LogWarning("after change rejected");
        logger.LogError("after change accepted");
        await host.StopAsync(testContext.CancellationToken);

        var entries = await ReadEntriesAsync(host);
        CollectionAssert.AreEqual(new[] { "before change", "after change accepted" },
            entries.Select(entry => entry.Message).ToArray());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_CustomEntry_CategoryRuleFiltersBeforeMapping()
    {
        using var host = CreateCustomHost();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CustomLoggingDbContext>();
            context.LogCategoryLevels.Add(new CategoryLevel { Category = "Default", Level = LogLevel.Error });
            await context.SaveChangesAsync(testContext.CancellationToken);
        }
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();
        logger.LogWarning("Filtered {Name}", "throw");
        logger.LogError("Retained {Name}", "world");
        await host.StopAsync(testContext.CancellationToken);

        await using var readScope = host.Services.CreateAsyncScope();
        var entry = Assert.ContainsSingle(await readScope.ServiceProvider.GetRequiredService<CustomLoggingDbContext>()
            .LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken));
        Assert.AreEqual("world", entry.Name);
        Assert.AreEqual("Retained world", entry.Message);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task StopAsync_ConcurrentEntries_DrainsQueueAndPreservesAllEntries()
    {
        using var host = CreateHost();
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();

        Parallel.For(0, 100, new ParallelOptions { CancellationToken = testContext.CancellationToken },
            index => logger.LogWarning("Item {Index} {Number}", index, double.NaN));
        await host.StopAsync(testContext.CancellationToken);

        var entries = await ReadEntriesAsync(host);
        Assert.HasCount(100, entries);
        CollectionAssert.AreEqual(Enumerable.Range(0, 100).ToArray(),
            entries.Select(entry => Property(entry, "Index").GetInt32()).Order().ToArray());
        foreach (var entry in entries)
        {
            Assert.AreEqual("Item {Index} {Number}", entry.Template);
            Assert.AreEqual("NaN", Property(entry, "Number").GetString());
        }
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task AddShambleLog_RegisteredTwice_PersistsOnlyOneCopy()
    {
        using var host = CreateHost(registerTwice: true);
        await host.StartAsync(testContext.CancellationToken);

        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("One entry");
        await host.StopAsync(testContext.CancellationToken);

        var entry = Assert.ContainsSingle(await ReadEntriesAsync(host));
        Assert.AreEqual("One entry", entry.Message);
        Assert.ContainsSingle(host.Services.GetServices<ILoggerProvider>().OfType<ShambleLogLoggerProvider>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddShambleLog_RegistersSingletonWriterAsHostedService(bool registerTwice)
    {
        using var host = CreateHost(registerTwice: registerTwice);

        var writer = host.Services.GetRequiredService<LogEntryWriter>();
        Assert.AreSame(writer, Assert.ContainsSingle(host.Services.GetServices<IHostedService>().OfType<LogEntryWriter>()));
        Assert.IsEmpty(host.Services.GetServices<IHostedService>().OfType<PurgeService>());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task AddShambleLog_WithPurgeOptions_RemovesExpiredEntries()
    {
        using var host = CreateHost(purgeServiceOptions: new PurgeServiceOptions
        {
            DeleteAfter = TimeSpan.FromDays(1),
            Interval = TimeSpan.FromHours(1)
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            context.LogEntries.Add(new LogEntry { Timestamp = DateTime.UtcNow.AddDays(-2), Message = "expired" });
            context.LogEntries.Add(new LogEntry { Timestamp = DateTime.UtcNow, Message = "retained" });
            await context.SaveChangesAsync(testContext.CancellationToken);
        }

        await host.StartAsync(testContext.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<LogEntry> entries;
        do
        {
            entries = await ReadEntriesAsync(host);
            if (entries.All(entry => entry.Message != "expired"))
                break;
            await Task.Delay(10, testContext.CancellationToken);
        } while (DateTime.UtcNow < deadline);

        await host.StopAsync(testContext.CancellationToken);
        entries = await ReadEntriesAsync(host);

        Assert.IsFalse(entries.Any(entry => entry.Message == "expired"));
        Assert.AreEqual(1, entries.Count(entry => entry.Message == "retained"));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task LogEntryWriter_ExecuteTask_TracksHostLifecycle()
    {
        using var host = CreateHost();
        var writer = host.Services.GetRequiredService<LogEntryWriter>();
        Assert.IsNull(writer.ExecuteTask);

        await host.StartAsync(testContext.CancellationToken);
        var execution = writer.ExecuteTask;
        Assert.IsNotNull(execution);
        Assert.IsFalse(execution.IsCompleted);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsTrue(execution.IsCompletedSuccessfully);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task SaveChangesAsync_ProducesLogs_DoesNotReenqueueDatabaseLogs()
    {
        using var host = CreateHost();
        await host.StartAsync(testContext.CancellationToken);

        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("Application log");
        await host.StopAsync(testContext.CancellationToken);

        var entry = Assert.ContainsSingle(await ReadEntriesAsync(host));
        Assert.AreEqual("Application log", entry.Message);
        Assert.AreEqual(false, host.Services.GetRequiredService<WriteBehavior>().DatabaseLoggingEnabled);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task StopAsync_DatabaseWriteFails_PropagatesPersistenceFailure()
    {
        using var host = CreateHost(failWrites: true);
        await host.StartAsync(testContext.CancellationToken);
        var writer = host.Services.GetRequiredService<LogEntryWriter>();
        var execution = writer.ExecuteTask;
        Assert.IsNotNull(execution);
        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("Fail this write");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StopAsync(testContext.CancellationToken));

        Assert.AreEqual("Simulated database failure", exception.Message);
        Assert.IsTrue(execution.IsFaulted);
        Assert.AreSame(exception, execution.Exception?.InnerException);
        Assert.IsEmpty(await ReadEntriesAsync(host));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_CustomEntry_PersistsExtractedColumnAndOriginalValues()
    {
        using var host = CreateCustomHost();
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();
        var exception = new InvalidOperationException("custom failure");
        var before = DateTime.UtcNow;
        using (logger.BeginScope("Scope {ScopeId}", 7))
        {
            logger.LogWarning(new EventId(12, "Test"), exception, "Hello {Name}", "world");
        }
        var after = DateTime.UtcNow;
        await host.StopAsync(testContext.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var entry = Assert.ContainsSingle(await scope.ServiceProvider.GetRequiredService<CustomLoggingDbContext>()
            .LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken));
        Assert.AreEqual("world", entry.Name);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        Assert.AreEqual(typeof(LoggingIntegrationTests).FullName, entry.Category);
        Assert.AreEqual("Hello {Name}", entry.Template);
        Assert.AreEqual("Hello world", entry.Message);
        Assert.AreEqual(exception.ToString(), entry.Exception);
        Assert.AreEqual(DateTimeKind.Utc, entry.Timestamp.Kind);
        Assert.IsTrue(entry.Timestamp >= before && entry.Timestamp <= after);
        Assert.AreEqual("world", Property(entry, "Name").GetString());
        Assert.AreEqual(12, Property(entry, "EventId").GetInt32());
        Assert.AreEqual("Test", Property(entry, "EventName").GetString());
        Assert.AreEqual("Scope 7", Assert.ContainsSingle(Property(entry, "Scopes").EnumerateArray()).GetString());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task StopAsync_CustomEntrySetPropertiesFails_PropagatesFailure()
    {
        using var host = CreateCustomHost();
        await host.StartAsync(testContext.CancellationToken);
        var writer = host.Services.GetRequiredService<LogEntryWriter>();
        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("Fail {Name}", "throw");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StopAsync(testContext.CancellationToken));

        Assert.AreEqual("Custom mapping failed", exception.Message);
        Assert.IsTrue(writer.ExecuteTask!.IsFaulted);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.IsEmpty(await scope.ServiceProvider.GetRequiredService<CustomLoggingDbContext>()
            .LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken));
    }

    private static IHost CreateCustomHost()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddShambleLog<CustomLoggingDbContext, CustomLogEntry>();
            })
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                services.AddDbContext<CustomLoggingDbContext>(options =>
                    options.UseInMemoryDatabase("custom-logs", databaseRoot));
            })
            .Build();
    }

    private static IHost CreateHost(
        bool failWrites = false,
        bool registerTwice = false,
        PurgeServiceOptions? purgeServiceOptions = null)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddShambleLog<LoggingTestDbContext>(purgeServiceOptions);
                if (registerTwice)
                    logging.AddShambleLog<LoggingTestDbContext>(purgeServiceOptions);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(new WriteBehavior(failWrites));
                services.Configure<HostOptions>(options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                services.AddDbContext<LoggingTestDbContext>(options =>
                    options.UseInMemoryDatabase("logs", databaseRoot));
            })
            .Build();
    }

    private async Task<List<LogEntry>> ReadEntriesAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>()
            .LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken);
    }

    private static JsonElement Property(LogEntry entry, string name) =>
        Assert.IsInstanceOfType<JsonElement>(entry.Properties[name]);

    public sealed class CustomLogEntry : LogEntry
    {
        public string? Name { get; set; }

        public override void SetProperties(string sourceContext, LogLevel level, string template, string message,
            string? exception, Dictionary<string, object?> properties)
        {
            base.SetProperties(sourceContext, level, template, message, exception, properties);
            Name = properties.TryGetValue("Name", out var name) ? name as string : null;
            if (Name == "throw")
                throw new InvalidOperationException("Custom mapping failed");
        }
    }

    private sealed class CustomLoggingDbContext(DbContextOptions<CustomLoggingDbContext> options)
        : DbContext(options), ILoggingDbContext<CustomLogEntry>
    {
        public DbSet<CustomLogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CustomLogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => SerializeProperties(properties),
                json => DeserializeProperties(json));

        private static string SerializeProperties(Dictionary<string, object?> properties) =>
            JsonSerializer.Serialize(properties);

        private static Dictionary<string, object?> DeserializeProperties(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
            ?? throw new InvalidOperationException("Invalid log properties JSON.");
    }

    private sealed record WriteBehavior(bool FailWrites)
    {
        public bool? DatabaseLoggingEnabled { get; set; }
        public SemaphoreSlim EntrySaved { get; } = new(0);
    }

    private sealed class LoggingTestDbContext(
        DbContextOptions<LoggingTestDbContext> options,
        ILogger<LoggingTestDbContext> logger,
        WriteBehavior behavior) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        public Task<int> SaveRulesAsync(CancellationToken cancellationToken) => base.SaveChangesAsync(cancellationToken);

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            behavior.DatabaseLoggingEnabled = logger.IsEnabled(LogLevel.Warning);
            logger.LogWarning("A database log must not be re-enqueued");
            if (behavior.FailWrites)
                throw new InvalidOperationException("Simulated database failure");
            var result = await base.SaveChangesAsync(cancellationToken);
            behavior.EntrySaved.Release();
            return result;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => SerializeProperties(properties),
                json => DeserializeProperties(json));

        private static string SerializeProperties(Dictionary<string, object?> properties) =>
            JsonSerializer.Serialize(properties);

        private static Dictionary<string, object?> DeserializeProperties(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
            ?? throw new InvalidOperationException("Invalid log properties JSON.");
    }
}
