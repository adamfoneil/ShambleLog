# ShambleLog

ShambleLog adds an `ILogger<T>` provider that writes log entries to an Entity Framework Core context in the background. Register your own database provider and context, then add the logger to a .NET generic host:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlServer(connectionString)); // Or another EF Core provider.
builder.Logging.AddShambleLog<AppDbContext>();
```

The context must derive from `DbContext`, implement `ILoggingDbContext`, and expose a `DbSet<LogEntry>`. The inherited `SaveChangesAsync` implements the interface. Configure the `Properties` dictionary for your database; for example, use a JSON value converter:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
	: DbContext(options), ILoggingDbContext
{
	public DbSet<LogEntry> LogEntries { get; set; } = null!;

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

The standard logging filters apply. The provider records the UTC timestamp, level, category, message template, formatted message, exception text, structured properties, event ID/name, and formatted scopes. Finite numeric, boolean, string, and null property values are retained; other values, including non-finite numbers, are converted to strings at log time. `EventId`, `EventName`, and `Scopes` are reserved property names. Avoid logging sensitive data.

A single hosted worker saves entries in order using a new DI scope per entry. Logs produced by the worker's own database operations are excluded from this provider to prevent recursion; other providers still receive them. The unbounded in-memory queue is not durable and can grow if writes are slower than logging. Normal host shutdown completes the queue and drains it within the host's shutdown timeout; entries logged after completion cannot be saved and a warning is written to standard error. A database failure is reported to standard error and faults the background service (normally stopping the host); there is no retry or crash recovery. Use a host and register the context before starting it.

## Tests

`ShambleLog.Core.Tests` contains MSTest integration tests using isolated EF Core in-memory databases. Run them in Visual Studio Test Explorer or with `dotnet test ShambleLog.slnx`. The suite covers structured log data, filtering, concurrent writes and shutdown draining, duplicate registration, recursion prevention, and database failure propagation.
