using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShambleLog.Core;

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddShambleLog<TestContext>();
builder.Logging.AddShambleLog<TestContext>();
builder.Services.AddDbContext<TestContext>(options => options.UseInMemoryDatabase("verification"));
using var host = builder.Build();
await host.StartAsync();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Filtered {Value}", 1);
using (logger.BeginScope("Scope {ScopeId}", 7))
{
    logger.LogWarning(new EventId(12, "Test"), new InvalidOperationException("failure"), "Hello {Name} {Count}", "world", 42);
}
Parallel.For(0, 100, i => logger.LogWarning("Item {Index} {Number}", i, double.NaN));
await host.StopAsync();
using var scope = host.Services.CreateScope();
var rows = await scope.ServiceProvider.GetRequiredService<TestContext>().LogEntries.ToListAsync();
if (rows.Count != 101) throw new Exception($"Expected 101 rows, got {rows.Count}");
var indices = rows.Where(x => x.Template == "Item {Index} {Number}").Select(x => int.Parse(x.Properties["Index"]!.ToString()!)).Order().ToArray();
if (!indices.SequenceEqual(Enumerable.Range(0, 100)) || rows.Skip(1).Any(x => x.Properties["Number"]!.ToString() != "NaN")) throw new Exception("Incorrect concurrent log entries");
var row = rows[0];
if (row.Level != LogLevel.Warning || row.SourceContext != typeof(Program).FullName || row.Template != "Hello {Name} {Count}" || row.Message != "Hello world 42" || row.Exception?.Contains("failure") != true) throw new Exception("Incorrect basic log data");
if (row.Properties["Name"]?.ToString() != "world" || row.Properties["Count"]?.ToString() != "42" || row.Properties["EventId"]?.ToString() != "12" || row.Properties["EventName"]?.ToString() != "Test" || row.Properties["Scopes"]?.ToString()?.Contains("Scope 7") != true) throw new Exception("Incorrect structured data");

var failureBuilder = Host.CreateApplicationBuilder();
failureBuilder.Logging.ClearProviders();
failureBuilder.Logging.AddShambleLog<TestContext>();
failureBuilder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
failureBuilder.Services.AddDbContext<TestContext>(options => options.UseInMemoryDatabase("failure"));
using var failureHost = failureBuilder.Build();
TestContext.FailWrites = true;
await failureHost.StartAsync();
failureHost.Services.GetRequiredService<ILogger<Program>>().LogWarning("Fail this write");
try
{
    await failureHost.StopAsync();
    throw new Exception("Expected a persistence failure");
}
catch (InvalidOperationException ex) when (ex.Message == "Simulated database failure") { }
Console.WriteLine("Integration verification passed.");

public sealed class TestContext(DbContextOptions<TestContext> options, ILogger<TestContext> logger) : DbContext(options), ILoggingDbContext
{
    public static bool FailWrites { get; set; }
    public DbSet<LogEntry> LogEntries { get; set; } = null!;
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        logger.LogWarning("A database log must not be re-enqueued");
        if (FailWrites) throw new InvalidOperationException("Simulated database failure");
        return base.SaveChangesAsync(cancellationToken);
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<LogEntry>().Property(x => x.Properties)
        .HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null), value => JsonSerializer.Deserialize<Dictionary<string, object?>>(value, (JsonSerializerOptions?)null)!);
}
