using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        Assert.AreEqual(typeof(LoggingIntegrationTests).FullName, entry.SourceContext);
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
        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("Fail this write");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StopAsync(testContext.CancellationToken));

        Assert.AreEqual("Simulated database failure", exception.Message);
        Assert.IsEmpty(await ReadEntriesAsync(host));
    }

    private static IHost CreateHost(bool failWrites = false, bool registerTwice = false)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddShambleLog<LoggingTestDbContext>();
                if (registerTwice)
                    logging.AddShambleLog<LoggingTestDbContext>();
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

    private sealed record WriteBehavior(bool FailWrites)
    {
        public bool? DatabaseLoggingEnabled { get; set; }
    }

    private sealed class LoggingTestDbContext(
        DbContextOptions<LoggingTestDbContext> options,
        ILogger<LoggingTestDbContext> logger,
        WriteBehavior behavior) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            behavior.DatabaseLoggingEnabled = logger.IsEnabled(LogLevel.Warning);
            logger.LogWarning("A database log must not be re-enqueued");
            if (behavior.FailWrites)
                throw new InvalidOperationException("Simulated database failure");
            return base.SaveChangesAsync(cancellationToken);
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
