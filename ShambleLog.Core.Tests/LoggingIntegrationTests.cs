using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;

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
    public async Task AddShambleLog_ConfiguredLevels_SeedsMissingRulesAndDatabaseWinsConflicts()
    {
        var warnings = new CapturingLoggerProvider();
        using var host = CreateConfiguredHost(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Example"] = "Error",
            ["Logging:LogLevel:New.Category"] = "None"
        }, warnings, registerTwice: true);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            context.LogCategoryLevels.Add(new CategoryLevel { Category = "example", Level = LogLevel.Debug });
            await context.SaveRulesAsync(testContext.CancellationToken);
        }

        await host.StartAsync(testContext.CancellationToken);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Example.Child").LogDebug("database permits debug");
        factory.CreateLogger("Other").LogInformation("default rejects information");
        factory.CreateLogger("Other").LogWarning("default permits warning");
        factory.CreateLogger("New.Category").LogCritical("disabled");
        await host.StopAsync(testContext.CancellationToken);

        await using var readScope = host.Services.CreateAsyncScope();
        var rules = await readScope.ServiceProvider.GetRequiredService<LoggingTestDbContext>()
            .LogCategoryLevels.AsNoTracking().ToListAsync(testContext.CancellationToken);
        Assert.HasCount(3, rules);
        Assert.AreEqual(LogLevel.Debug, rules.Single(rule => rule.Category == "example").Level);
        Assert.AreEqual(LogLevel.Warning, rules.Single(rule => rule.Category == "Default").Level);
        Assert.AreEqual(LogLevel.None, rules.Single(rule => rule.Category == "New.Category").Level);
        var messages = (await ReadEntriesAsync(host)).Select(entry => entry.Message).ToArray();
        CollectionAssert.Contains(messages, "database permits debug");
        CollectionAssert.Contains(messages, "default permits warning");
        CollectionAssert.DoesNotContain(messages, "default rejects information");
        CollectionAssert.DoesNotContain(messages, "disabled");
        var warning = Assert.ContainsSingle(warnings.Messages.Where(message => message.Contains("takes precedence")));
        StringAssert.Contains(warning, "Example");
        StringAssert.Contains(warning, "Error");
        StringAssert.Contains(warning, "Debug");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task AddShambleLog_ExistingMatchingRule_DoesNotWarn()
    {
        var warnings = new CapturingLoggerProvider();
        using var host = CreateConfiguredHost(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Example"] = "Warning"
        }, warnings);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>();
            context.LogCategoryLevels.Add(new CategoryLevel { Category = "example", Level = LogLevel.Warning });
            await context.SaveRulesAsync(testContext.CancellationToken);
        }
        await host.StartAsync(testContext.CancellationToken);
        await host.StopAsync(testContext.CancellationToken);

        await using var readScope = host.Services.CreateAsyncScope();
        var rule = await readScope.ServiceProvider.GetRequiredService<LoggingTestDbContext>()
            .LogCategoryLevels.SingleAsync(testContext.CancellationToken);
        Assert.AreEqual("example", rule.Category);
        Assert.AreEqual(LogLevel.Warning, rule.Level);
        Assert.IsFalse(warnings.Messages.Any(message => message.Contains("takes precedence")));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task AddShambleLog_QueuedBeforeStart_UsesInitializedRules()
    {
        using var host = CreateConfiguredHost(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Error"
        }, new CapturingLoggerProvider());
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BeforeStart").LogWarning("filtered before start");
        await host.StartAsync(testContext.CancellationToken);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BeforeStart").LogError("accepted after start");
        await host.StopAsync(testContext.CancellationToken);

        var entry = Assert.ContainsSingle(await ReadEntriesAsync(host));
        Assert.AreEqual("accepted after start", entry.Message);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task AddShambleLog_InvalidConfiguredLevel_FailsStartup()
    {
        using var host = CreateConfiguredHost(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "NotALevel"
        }, new CapturingLoggerProvider(), applyLoggingConfiguration: false);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StartAsync(testContext.CancellationToken));
        StringAssert.Contains(exception.Message, "Logging:LogLevel:Default");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_Middleware_RunsInOrderBranchesRejoinAndCanShortCircuit()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = CreateLinkedHost(shamble => shamble
            .Use(async (context, next) =>
            {
                calls.Enqueue($"first:{context.Entry.Message}");
                if (context.Entry.Message == "skip")
                    return;
                await next(context);
                calls.Enqueue($"after-save:{context.Entry.Id != 0}");
            })
            .UseForCategory("linked", branch => branch.Use((context, next) =>
            {
                calls.Enqueue($"branch:{context.Entry.Category}");
                return next(context);
            }))
            .Use((context, next) =>
            {
                calls.Enqueue($"last:{context.Entry.Message}");
                return next(context);
            }));
        await host.StartAsync(testContext.CancellationToken);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Linked.Child").LogWarning("linked");
        factory.CreateLogger("Other").LogWarning("other");
        factory.CreateLogger("Linked.Child").LogWarning("skip");
        await host.StopAsync(testContext.CancellationToken);

        CollectionAssert.AreEqual(new[]
        {
            "first:linked", "branch:Linked.Child", "last:linked", "after-save:True",
            "first:other", "last:other", "after-save:True",
            "first:skip"
        }, calls.ToArray());
        await using var scope = host.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>()
            .LogEntries.AsNoTracking().Select(entry => entry.Message).ToListAsync(testContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { "linked", "other" }, messages);
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
    public async Task Log_Middleware_CategoryRuleFiltersBeforePipeline()
    {
        using var host = CreateLinkedHost(shamble => shamble.Use<NameDetailMiddleware>());
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>();
            context.LogCategoryLevels.Add(new CategoryLevel { Category = "Default", Level = LogLevel.Error });
            await context.SaveChangesAsync(testContext.CancellationToken);
        }
        await host.StartAsync(testContext.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>();
        logger.LogWarning("Filtered {Name}", "throw");
        logger.LogError("Retained {Name}", "world");
        await host.StopAsync(testContext.CancellationToken);

        await using var readScope = host.Services.CreateAsyncScope();
        var detail = Assert.ContainsSingle(await readScope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>()
            .Details.AsNoTracking().Include(item => item.LogEntry).ToListAsync(testContext.CancellationToken));
        Assert.AreEqual("world", detail.Name);
        Assert.AreEqual("Retained world", detail.LogEntry.Message);
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
    public async Task AddShambleLog_MiddlewareOverload_WithPurgeOptions_RemovesExpiredEntries()
    {
        var options = new PurgeServiceOptions { DeleteAfter = TimeSpan.FromDays(1), Interval = TimeSpan.FromHours(2) };
        using var host = CreateLinkedHost(shamble => shamble.Use<NameDetailMiddleware>(), options, registerTwice: true);
        options.DeleteAfter = TimeSpan.Zero;
        options.Interval = TimeSpan.FromSeconds(1);

        var configured = host.Services.GetRequiredService<IOptions<PurgeServiceOptions>>().Value;
        Assert.AreEqual(TimeSpan.FromDays(1), configured.DeleteAfter);
        Assert.AreEqual(TimeSpan.FromHours(2), configured.Interval);
        Assert.ContainsSingle(host.Services.GetServices<IHostedService>().OfType<PurgeService>());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>();
            context.LogEntries.Add(new LogEntry { Timestamp = DateTime.UtcNow.AddDays(-2), Message = "expired" });
            context.LogEntries.Add(new LogEntry { Timestamp = DateTime.UtcNow, Message = "retained" });
            await context.SaveChangesAsync(testContext.CancellationToken);
        }

        await host.StartAsync(testContext.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<LogEntry> entries;
        do
        {
            await using var scope = host.Services.CreateAsyncScope();
            entries = await scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>()
                .LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken);
            if (entries.All(entry => entry.Message != "expired"))
                break;
            await Task.Delay(10, testContext.CancellationToken);
        } while (DateTime.UtcNow < deadline);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsFalse(entries.Any(entry => entry.Message == "expired"));
        Assert.AreEqual(1, entries.Count(entry => entry.Message == "retained"));
    }

    [TestMethod]
    public void AddShambleLog_MiddlewareOverload_WithoutPurgeOptions_DoesNotRegisterPurgeService()
    {
        using var host = CreateLinkedHost(shamble => shamble.Use<NameDetailMiddleware>());
        Assert.IsEmpty(host.Services.GetServices<IHostedService>().OfType<PurgeService>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddShambleLog_MixedOverloads_RegisterOnePurgeService(bool middlewareFirst)
    {
        var purgeOptions = new PurgeServiceOptions
        {
            DeleteAfter = TimeSpan.FromDays(7),
            Interval = TimeSpan.FromHours(2)
        };
        using var host = new HostBuilder()
            .ConfigureLogging(logging =>
            {
                if (middlewareFirst)
                {
                    logging.AddShambleLog<LoggingTestDbContext>(shamble => shamble.Use((context, next) => next(context)), purgeOptions);
                    logging.AddShambleLog<LoggingTestDbContext>();
                }
                else
                {
                    logging.AddShambleLog<LoggingTestDbContext>();
                    logging.AddShambleLog<LoggingTestDbContext>(shamble => shamble.Use((context, next) => next(context)), purgeOptions);
                }
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(new WriteBehavior(false));
                services.AddDbContext<LoggingTestDbContext>(options => options.UseInMemoryDatabase("mixed-overloads"));
            })
            .Build();
        purgeOptions.DeleteAfter = TimeSpan.Zero;
        purgeOptions.Interval = TimeSpan.FromSeconds(1);

        var configured = host.Services.GetRequiredService<IOptions<PurgeServiceOptions>>().Value;
        Assert.AreEqual(TimeSpan.FromDays(7), configured.DeleteAfter);
        Assert.AreEqual(TimeSpan.FromHours(2), configured.Interval);
        Assert.ContainsSingle(host.Services.GetServices<IHostedService>().OfType<PurgeService>());
        Assert.ContainsSingle(host.Services.GetServices<IHostedService>().OfType<LogEntryWriter>());
        using var scope = host.Services.CreateScope();
        Assert.AreSame(scope.ServiceProvider.GetRequiredService<LoggingTestDbContext>(),
            scope.ServiceProvider.GetRequiredService<ILoggingDbContext>());
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
    public async Task Log_EFCoreMiddleware_PersistsExecutedCommandDetails()
    {
        const string template = "Executed DbCommand ({elapsed}ms) [Parameters=[{parameters}], CommandType='{commandType}', CommandTimeout='{commandTimeout}']{newLine}{commandText}";
        const string sql = "-- GetOrders\n-- GetOrders\nSELECT o.\"Id\" FROM \"Orders\" AS o INNER JOIN \"Customers\" AS c ON o.\"CustomerId\" = c.\"Id\"";
        using var host = CreateLinkedHost(shamble => shamble.UseEFCore());
        await host.StartAsync(testContext.CancellationToken);
        var efLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(EFCoreMiddleware.Category);
        efLogger.LogWarning(new EventId(20101, "CommandExecuted"), template, "1,234", "@p0='5'", "Text", 30, "\n", sql);
        efLogger.LogWarning(new EventId(20100, "CommandExecuting"), "Executing DbCommand {commandText}", sql);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Other")
            .LogWarning(new EventId(20101), template, "1", "", "Text", 30, "\n", sql);
        await host.StopAsync(testContext.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>();
        Assert.HasCount(3, await context.LogEntries.ToListAsync(testContext.CancellationToken));
        var command = Assert.ContainsSingle(await context.EFCoreEntries.Include(item => item.LogEntry)
            .ToListAsync(testContext.CancellationToken));
        Assert.AreEqual(EFCoreMiddleware.Category, command.LogEntry.Category);
        Assert.AreEqual(20101, Property(command.LogEntry, "EventId").GetInt32());
        Assert.AreEqual(1234, command.ElapsedMs);
        Assert.AreEqual("@p0='5'", command.Parameters);
        Assert.AreEqual(sql, command.CommandText);
        CollectionAssert.AreEqual(new[] { "GetOrders" }, command.Tags);
        CollectionAssert.AreEqual(new[] { "Orders", "Customers" }, command.TargetTables);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Log_CategoryMiddleware_PersistsLinkedEntityForMatchingCategory()
    {
        using var host = CreateLinkedHost(shamble => shamble
            .UseForCategory("Linked", branch => branch.Use<NameDetailMiddleware>()));
        await host.StartAsync(testContext.CancellationToken);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Linked.Child").LogWarning("Hello {Name}", "world");
        factory.CreateLogger("Other").LogWarning("Hello {Name}", "other");
        await host.StopAsync(testContext.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>();
        var entries = await context.LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken);
        Assert.HasCount(2, entries);
        var detail = Assert.ContainsSingle(await context.Details.AsNoTracking().ToListAsync(testContext.CancellationToken));
        var linkedEntry = entries.Single(entry => entry.Id == detail.LogEntryId);
        Assert.AreEqual("Linked.Child", linkedEntry.Category);
        Assert.AreEqual("Hello world", linkedEntry.Message);
        Assert.AreEqual("world", detail.Name);
        Assert.AreEqual("world", Property(linkedEntry, "Name").GetString());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task StopAsync_MiddlewareFails_PropagatesFailureWithoutSaving()
    {
        using var host = CreateLinkedHost(shamble => shamble.Use<NameDetailMiddleware>());
        await host.StartAsync(testContext.CancellationToken);
        var writer = host.Services.GetRequiredService<LogEntryWriter>();
        host.Services.GetRequiredService<ILogger<LoggingIntegrationTests>>().LogWarning("Fail {Name}", "throw");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StopAsync(testContext.CancellationToken));

        Assert.AreEqual("Middleware failed", exception.Message);
        Assert.IsTrue(writer.ExecuteTask!.IsFaulted);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkedLoggingDbContext>();
        Assert.IsEmpty(await context.LogEntries.AsNoTracking().ToListAsync(testContext.CancellationToken));
        Assert.IsEmpty(await context.Details.AsNoTracking().ToListAsync(testContext.CancellationToken));
    }

    private static IHost CreateConfiguredHost(Dictionary<string, string?> values, CapturingLoggerProvider warnings,
        bool registerTwice = false, bool applyLoggingConfiguration = true)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                if (applyLoggingConfiguration)
                    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddProvider(warnings);
                logging.AddShambleLog<LoggingTestDbContext>();
                if (registerTwice)
                    logging.AddShambleLog<LoggingTestDbContext>();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(new WriteBehavior(false));
                services.AddDbContext<LoggingTestDbContext>(options => options.UseInMemoryDatabase("configured-logs", databaseRoot));
            })
            .Build();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private static IHost CreateLinkedHost(Action<ShambleLogBuilder> configure,
        PurgeServiceOptions? purgeServiceOptions = null, bool registerTwice = false)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddShambleLog<LinkedLoggingDbContext>(configure, purgeServiceOptions);
                if (registerTwice)
                    logging.AddShambleLog<LinkedLoggingDbContext>(purgeServiceOptions);
            })
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                services.AddDbContext<LinkedLoggingDbContext>(options =>
                    options.UseInMemoryDatabase("linked-logs", databaseRoot));
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

    public sealed class LinkedLogDetail
    {
        public long Id { get; set; }
        public long LogEntryId { get; set; }
        public LogEntry LogEntry { get; set; } = null!;
        public string? Name { get; set; }
    }

    private sealed class NameDetailMiddleware(LinkedLoggingDbContext context) : ILogEntryMiddleware
    {
        public Task InvokeAsync(LogEntryContext logContext, LogEntryDelegate next)
        {
            var name = logContext.Entry.Properties.TryGetValue("Name", out var value) ? value as string : null;
            if (name == "throw")
                throw new InvalidOperationException("Middleware failed");
            context.Details.Add(new LinkedLogDetail { LogEntry = logContext.Entry, Name = name });
            return next(logContext);
        }
    }

    private sealed class LinkedLoggingDbContext(DbContextOptions<LinkedLoggingDbContext> options)
        : DbContext(options), IEFCoreLoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;
        public DbSet<LinkedLogDetail> Details { get; set; } = null!;
        public DbSet<EFCoreEntry> EFCoreEntries { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => SerializeProperties(properties),
                json => DeserializeProperties(json));
            modelBuilder.Entity<LinkedLogDetail>()
                .HasOne(detail => detail.LogEntry)
                .WithMany()
                .HasForeignKey(detail => detail.LogEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<EFCoreEntry>(entry =>
            {
                entry.HasKey(item => item.LogEntryId);
                entry.HasOne(item => item.LogEntry).WithOne()
                    .HasForeignKey<EFCoreEntry>(item => item.LogEntryId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

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
