# ShambleLog

ShambleLog adds an `ILogger<T>` provider that writes log entries to an Entity Framework Core context in the background. The PostgreSQL project includes a context configured for the standard log tables:

```csharp
using Microsoft.EntityFrameworkCore;
using ShambleLog.Core;
using ShambleLog.PostgreSQL;

builder.Services.AddDbContext<ShambleLogDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("ShambleLog")));
builder.Logging.AddShambleLog<ShambleLogDbContext>();
```

The PostgreSQL context stores structured properties as `jsonb`. No migration is included yet: create and apply a migration for this context before starting the host.

For EF Core tooling, install `dotnet-ef` matching the project's EF Core version (11.0.0). Pass exactly one connection string after `--`; EF forwards it to the design-time factory. This does not affect the application's runtime configuration. For example, from the solution directory in PowerShell:

```powershell
$connectionString = 'Host=localhost;Database=shamblelog;Username=postgres'
dotnet ef migrations add InitialCreate --project ShambleLog.PostgreSQL --startup-project ShambleLog.PostgreSQL --context ShambleLogDbContext --output-dir Migrations -- $connectionString
dotnet ef database update --project ShambleLog.PostgreSQL --startup-project ShambleLog.PostgreSQL --context ShambleLogDbContext -- $connectionString
```

Creating a migration does not require a live database; applying it requires a reachable database and sufficient permissions. Command-line arguments may be visible to other users or diagnostic tools, so avoid passing credentials in the connection string. Use an appropriate PostgreSQL authentication mechanism and keep secrets out of source control.

Alternatively, register your own database provider and context, then add the logger to a .NET generic host:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlServer(connectionString)); // Or another EF Core provider.
builder.Logging.AddShambleLog<AppDbContext>();
```

Import `ShambleLog.Core` for the logging extensions, including when using the PostgreSQL project. A custom context must derive from `DbContext`, implement `ILoggingDbContext`, and expose `DbSet<LogEntry>` and `DbSet<CategoryLevel>` properties. The inherited `SaveChangesAsync` implements the interface. Configure the `Properties` dictionary for your database; for example, use a JSON value converter:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
	: DbContext(options), ILoggingDbContext
{
	public DbSet<LogEntry> LogEntries { get; set; } = null!;
	public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties)
			.HasConversion(
				properties => SerializeProperties(properties),
				json => DeserializeProperties(json));
	}

	private static string SerializeProperties(Dictionary<string, object?> properties) =>
		System.Text.Json.JsonSerializer.Serialize(properties);

	private static Dictionary<string, object?> DeserializeProperties(string json) =>
		System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
		?? throw new InvalidOperationException("Invalid log properties JSON.");
}
```

Create and apply the appropriate EF Core migration before using the provider. JSON values deserialized as `object` are represented by `JsonElement`; applications modifying tracked dictionaries should configure an EF Core value comparer as well.

The provider records the UTC timestamp, level, category, message template, formatted message, exception text, structured properties, event ID/name, and formatted scopes. Finite numeric, boolean, string, and null property values are retained; other values, including non-finite numbers, are converted to strings at log time. `EventId`, `EventName`, and `Scopes` are reserved property names. Avoid logging sensitive data.

At host startup, ShambleLog reads `Logging:LogLevel` from the registered `IConfiguration` (including appsettings.json) and inserts missing category rules into `LogCategoryLevels` before processing queued entries. Category names are compared case-insensitively. Existing database rows are never overwritten; a different configured level produces a warning containing both levels, subject to the receiving providers' filters. The table must already exist and be accessible when the host starts. Invalid configured levels or database failures prevent startup (the .NET logging configuration may reject an invalid level during host construction). Initialization runs once per host start, not on configuration reload; newly configured categories are added on the next start. Concurrent hosts initializing the same missing category may race on the database key and fail startup.

When `Logging:LogLevel` has entries, ShambleLog adds a provider-specific Trace filter so those global configuration rules do not discard messages before the database gate; other providers retain their normal filters. More-specific ShambleLog provider filters can still discard messages, and `ILogger.IsEnabled` does not reflect the database gate. With no `Logging:LogLevel` entries, standard logging filters continue to apply normally.

`LogCategoryLevels` controls an additional persistence gate. Each row sets the inclusive minimum `LogLevel` for a category prefix; `None` disables that prefix. Prefix matching is ordinal and case-insensitive, and the longest matching prefix wins. `Default` is reserved for the fallback rule when no other prefix matches. With no matching rule and no `Default`, entries pass the database gate. Category is the primary key; avoid defining category names that differ only by case. If such rows exist, ties are resolved by ordinal category order.

The writer reads the rules for each queued entry, so committed updates apply when an entry is processed, including entries already queued. This adds a database read per entry. The gate does not change `ILogger.IsEnabled` or prevent formatting and queuing; applicable provider-specific logging filters can still discard entries before this gate. Rule-read failures fault the writer like other database failures. Configure the category-level table in the EF model and apply a migration before enabling the provider.

A single hosted worker saves entries in order using a new DI scope per entry. Logs produced by the worker's own database operations are excluded from this provider to prevent recursion; other providers still receive them. The unbounded in-memory queue is not durable and can grow if writes are slower than logging. Normal host shutdown completes the queue and drains it within the host's shutdown timeout; entries logged after completion cannot be saved and a warning is written to standard error. A database failure is reported to standard error and faults the background service (normally stopping the host); there is no retry or crash recovery. Use a host and register the context before starting it.

The hosted `LogEntryWriter` is also available as a singleton from dependency injection. Its inherited `ExecuteTask` is null before the host starts, runs while the writer is active, and completes or faults when the writer stops. For example, resolve it with `host.Services.GetRequiredService<LogEntryWriter>()` and inspect `ExecuteTask`. A running task does not guarantee database connectivity or that the queue is keeping up. Let the host manage the writer's lifecycle.

To enable periodic deletion of old entries, pass `new PurgeServiceOptions { DeleteAfter = TimeSpan.FromDays(30), Interval = TimeSpan.FromHours(1) }` to `AddShambleLog<AppDbContext>(...)`, either alone or after a middleware configuration callback. Purging starts with the host and runs immediately, then at the configured interval; without options, no purge worker is registered. Set `DeleteAfter` explicitly to the desired retention period.

## Log entry middleware

To capture extra data in your own tables, add middleware when registering ShambleLog. Middleware runs in registration order for each entry that passes the category-level gate, inside the writer's per-entry DI scope. The final step adds the standard `LogEntry` and calls `SaveChangesAsync` once, so linked entities added before calling `next` are saved together with the entry:

```csharp
builder.Logging.AddShambleLog<AppDbContext>(shamble => shamble
	.UseForCategory("Microsoft.EntityFrameworkCore.Database.Command", ef => ef
		.Use<EfCommandLogMiddleware>()));

public sealed class EfCommandLog
{
	public long Id { get; set; }
	public long LogEntryId { get; set; }
	public LogEntry LogEntry { get; set; } = null!;
	public string? CommandText { get; set; }
}

public sealed class EfCommandLogMiddleware(AppDbContext db) : ILogEntryMiddleware
{
	public Task InvokeAsync(LogEntryContext context, LogEntryDelegate next)
	{
		if (context.Entry.Properties.TryGetValue("commandText", out var commandText))
			db.Add(new EfCommandLog { LogEntry = context.Entry, CommandText = commandText as string });
		return next(context);
	}
}
```

Configure the relationship in your model, for example `modelBuilder.Entity<EfCommandLog>().HasOne(log => log.LogEntry).WithMany().HasForeignKey(log => log.LogEntryId).OnDelete(DeleteBehavior.Cascade)`, and add a migration. Use a database-level cascade so purging entries also removes linked rows.

- `Use<TMiddleware>()` resolves `TMiddleware` from the per-entry scope (registered as scoped unless you register it first), so it can inject your scoped `DbContext`. It is the same instance the writer saves through.
- `Use((context, next) => ...)` adds inline middleware.
- `UseForCategory(prefix, branch => ...)` runs the branch for categories starting with the prefix (ordinal, case-insensitive); `UseWhen(predicate, branch => ...)` accepts any condition. Branches rejoin the main pipeline.
- Code after `await next(context)` runs after the save, when `context.Entry.Id` is assigned.
- Not calling `next` skips persisting the entry and any remaining middleware.
- Middleware exceptions fault the writer just like database failures.
- Each `AddShambleLog` call with a configuration callback appends its middleware.

Structured property values are snapshotted at log time as described above, so non-scalar values arrive as strings. EF Core does not expose every command detail through `ILogger`, and parameter values are only logged when sensitive data logging is enabled; avoid persisting sensitive data.

## Tests

`ShambleLog.Core.Tests` contains MSTest integration tests using isolated EF Core in-memory databases. Run them in Visual Studio Test Explorer or with `dotnet test ShambleLog.slnx`. The suite covers structured log data, filtering, concurrent writes and shutdown draining, duplicate registration, log entry middleware, recursion prevention, and database failure propagation.
