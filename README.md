# ShambleLog

When I was working on my Blazor Template, I wanted to integrate a log provider with a really nice consume experience, since that is what I feel is missing in the Serilog ecosystem. Yes, you can use Seq or Graylog or something along those lines. But those don't have the experience I'm looking for, and they run as standalone apps. I wanted to integrate a log viewer as a Razor Class Library for truly seamless integration with consuming apps.

The other motivation is that I don't really like Serilog's relational database integration. It is a bit too opinionated and not flexible in the ways I want it to be. I wanted a more native EF Core experience, with a DbContext and migrations, so that I can use the same database provider as my app and have a more natural integration with my existing data model.

I made [Bramblelog](https://bramblelog.dev) as a cloud-native logging provider with the consume experience I was after, and to avoid having to configure log storage locally. But I felt that a cloud SaaS dependency was too heavy for a template, and a hard sell to begin with. So I made ShambleLog, a local logging provider that can be used with any .NET application.

ShambleLog adds an `ILogger<T>` provider that writes log entries to an EF Core context in the background. The PostgreSQL project includes a context configured for the standard log tables:

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

If neither configuration nor the database defines `Default`, initialization adds it at `Information`. An existing database default takes precedence, even when configuration has no default.

When `Logging:LogLevel` has entries, ShambleLog adds a provider-specific Trace filter so those global configuration rules do not discard messages before the database gate; other providers retain their normal filters. More-specific ShambleLog provider filters can still discard messages, and `ILogger.IsEnabled` does not reflect the database gate. With no `Logging:LogLevel` entries, standard logging filters continue to apply normally.

`LogCategoryLevels` controls an additional persistence gate. Each row sets the inclusive minimum `LogLevel` for a category prefix; `None` disables that prefix. Prefix matching is ordinal and case-insensitive, and the longest matching prefix wins. `Default` is reserved for the fallback rule when no other prefix matches. With no matching rule and no `Default`, entries pass the database gate. Category is the primary key; avoid defining category names that differ only by case. If such rows exist, ties are resolved by ordinal category order.

The writer reads the rules for each queued entry, so committed updates apply when an entry is processed, including entries already queued. This adds a database read per entry. The gate does not change `ILogger.IsEnabled` or prevent formatting and queuing; applicable provider-specific logging filters can still discard entries before this gate. Rule-read failures fault the writer like other database failures. Configure the category-level table in the EF model and apply a migration before enabling the provider.

Resolve the singleton `SuppressedCategoryTracker` from dependency injection to inspect its thread-safe `Snapshot` array of distinct `(string Category, LogLevel SuppressedLevel)` pairs. Each pair contains the rejected entry's category and level, not the configured minimum. Only category-gate rejections are tracked, not entries discarded by earlier logging filters or skipped by middleware. Snapshots are independent copies; tracking stays in memory for the service's lifetime and is not yet exposed in the UI.

A single hosted worker saves entries in order using a new DI scope per entry. Logs produced by the worker's own database operations are excluded from this provider to prevent recursion; other providers still receive them. The unbounded in-memory queue is not durable and can grow if writes are slower than logging. Normal host shutdown completes the queue and drains it within the host's shutdown timeout; entries logged after completion cannot be saved and a warning is written to standard error. A database failure is reported to standard error and faults the background service (normally stopping the host); there is no retry or crash recovery. Use a host and register the context before starting it.

The hosted `LogEntryWriter` is also available as a singleton from dependency injection. Its inherited `ExecuteTask` is null before the host starts, runs while the writer is active, and completes or faults when the writer stops. For example, resolve it with `host.Services.GetRequiredService<LogEntryWriter>()` and inspect `ExecuteTask`. A running task does not guarantee database connectivity or that the queue is keeping up. Let the host manage the writer's lifecycle.

`LogEntryWriter.IngestionRate` reports successfully persisted log entries per second, averaged over that writer's lifetime. Timing includes the category gate, middleware, database save, and scope cleanup for persisted entries, but excludes time waiting for queued entries and processing entries that are filtered, skipped by middleware, or fail before saving. The thread-safe value is zero until the first successful save and updates after each persisted entry; it retains its last value while idle or stopped. It is an in-memory metric, not stored in the database, and can be read from the singleton for a future UI.

To enable periodic deletion of old entries, configure `WithRetentionPeriod`:

```csharp
builder.Logging.AddShambleLog<AppDbContext>(shamble => shamble
    .WithRetentionPeriod(new RetentionPeriodOptions
    {
        DeleteAfter = TimeSpan.FromDays(30),
        Interval = TimeSpan.FromHours(1)
    }));
```

Purging starts with the host and runs immediately, then at the configured interval; without this option, no retention worker is registered. Set `DeleteAfter` explicitly to the desired retention period. Options are copied at registration.

## HTTP logging and identity

For ASP.NET Core applications, opt into either capability through the same configuration callback:

```csharp
builder.Logging.AddShambleLog<AppDbContext>(shamble => shamble
    .WithIdentityUser()
    .WithHttpRequestLogging());
```

`WithIdentityUser()` captures the authenticated `HttpContext.User.Identity.Name` at each log call, before queuing, in the reserved `IdentityUser` structured property. Anonymous users, identities without a name, and logs outside a request have no such property. It applies to all categories, including request-completion logs, not just HTTP logs. Authentication must run before the logs whose identity should be captured. Usernames can be sensitive; enable this only when appropriate for your privacy and retention policies.

`WithHttpRequestLogging()` installs an outer ASP.NET Core startup-filter middleware, before the application's configured middleware. It logs one `Information` entry after the application pipeline finishes, with `Method`, `Path` (including path base), `StatusCode`, and `DurationMs`. Carriage returns and line feeds in the method and path are escaped. An exception handler in the application pipeline can set the final status before the entry is written; an unhandled exception is recorded as 500 if the response has not started, or with the already-started status otherwise. Configure authentication and exception handling in the application pipeline, and ensure the request logging category (`ShambleLog.Core.RequestLoggingStartupFilter`) passes logging filters and the database gate. Other startup filters or server-level failures outside this middleware are not observed. Paths can contain sensitive data. Query strings, headers, request bodies and response content are not recorded or read by this feature.

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

### EF Core command middleware

`UseEFCore()` records executed and failed EF Core commands (`CommandExecuted`/`CommandError` in the `Microsoft.EntityFrameworkCore.Database.Command` category) as `EFCoreEntry` rows: command text, parameters, elapsed milliseconds, `TagWith` tags, and referenced table names (a best-effort regex parse of the SQL). Implement `IEFCoreLoggingDbContext` on your logging context and map the entity; the PostgreSQL project's `EFCoreEntryConfiguration` uses `LogEntryId` as a one-to-one key with cascade delete. `TargetTables` and `Tags` are string arrays (`text[]` on PostgreSQL); other providers may need a value converter.

```csharp
builder.Logging.AddShambleLog<AppDbContext>(shamble => shamble.UseEFCore());
```

EF Core logs commands at `Information`, so the category must pass your logging filters and category-level rules. Commands issued by ShambleLog's own writer are not logged.

### Querying logs

`LogQuery.ExecuteAsync` filters a selected UTC `Date`, with optional category, template and message substrings, an exact level, and `PropertiesContain`. Prefix `CategoryContains`, `TemplateContains`, or `MessageContains` with `!` or `-` to exclude entries containing the remaining substring (for example, `!HealthCheck`). Only the first character is removed; a prefix alone excludes all entries because every string contains the empty substring. Null, empty, or whitespace-only values do not filter. `CategoryContains` matches substrings case-insensitively by lowercasing both values; casing follows the query provider and runtime culture, so non-ASCII behavior can vary. Template and message matching retain the query provider's existing case behavior.

The log viewer displays timestamps in the host's local time, using `ddd M/d/yy` followed by the culture's short time format. When a date filter is applied, it displays only the time. The filter selects a UTC date, so the corresponding local dates can differ from the selected date and from each other. Each timestamp also shows its age relative to the current UTC time, recalculated when the entries are rendered.

`PropertiesContain` uses comma-separated `key:value` pairs, such as `UserId:123, Method:-Get`. All pairs must match; values are strings and match string-valued properties exactly, so `UserId:123` does not match a numeric JSON property. Prefix a value with `-` to exclude matching entries (including entries where the property is absent). Whitespace around keys and unquoted values is trimmed. Quote values to include commas or preserve whitespace, for example `Message:"hello, world"`; quoted values use JSON string escaping, such as `\"` for a quote and `\\` for a backslash. An empty string must be quoted (`Key:""`). Invalid syntax throws `FormatException`; the log viewer displays the error and retains the previous results. This shorthand replaces the previous JSON object syntax and no longer searches numeric, boolean, null, array, or object values.

`LogQuery` is an abstract base class. Construct `ShambleLog.PostgreSQL.PostgreSQLLogQuery` for PostgreSQL, which filters the JSONB column in the database, or `ShambleLog.Core.Queries.ClientSideLogQuery` for provider-independent filtering after retrieving the other matches. Both take an `ILoggingDbContext`. Existing callers that constructed `LogQuery` directly must switch to one of these implementations. Other providers can derive from `LogQuery` and implement its property-filtering hook.

`TimeExpression` is combined with the selected UTC date. Use an integer with `s`, `m`, `h`, or `d` for a time relative to now (for example `30m`), or a range from nearer to farther in the past (for example `1:2h`). Alternatively, use four-digit UTC clock times: `0930:1330` selects the inclusive interval from 09:30 to 13:30 on the selected date, and `0930` selects from 09:30 on that date up to now. Clock ranges cannot cross midnight. Results are newest first by default; append `:asc`, or use `asc` alone, for oldest first. Invalid expressions throw `FormatException`.

In `ShambleLog.Core`, `CategorySummaryQuery.ExecuteAsync` returns each category's counts by log level. Its optional UTC date and time expression use the same filtering as `LogQuery`; without a date, clock times use the current UTC date, and without either filter the summary includes all timestamps. Pass a time expression by name, for example `ExecuteAsync(timeExpression: "30m")`. `EFCoreMetricsQuery.ExecuteAsync` uses the same date and time filters and returns per-tag and per-table counts and elapsed-time statistics, including the nearest-rank 95th percentile. Names are trimmed and grouped case-insensitively; a command contributes once to each distinct tag or table, and commands without names produce no metric for that type.

### Managing categories

The RCL `LogViewer` accepts a `CategoryLevelQuery` and a `ScopedQueryRunner` from `ShambleLog.Core.Queries`. Register those Core services and the existing query classes with DI, inject the two services in the consuming page, and pass them to the component (see Demo). Register `LogQuery` with the provider-specific implementation (for example `PostgreSQLLogQuery`) and `CategorySummaryQuery` as scoped or transient services. Register `EFCoreMetricsQuery` as well if using it through the runner. The runner resolves each query in a new scope per operation, preventing concurrent operations from sharing the circuit's `ILoggingDbContext`; its context registration must be scoped, as with `AddShambleLog`. `CategoryLevelQuery` also uses a new scope per operation. The standalone `Categories` component accepts the same two Core services.

The manager lists stored rules and groups observed categories under the longest matching prefix, or `Default`. Changing an observed category's level creates a temporary rule for that exact category, or updates its existing rule without changing its temporary status. Temporary rules can be deleted to restore inherited behavior. `Default` can be edited but not renamed or deleted. Refresh reloads the rules and summaries using the current UTC date and time expression from the viewer's FilterBar; changing either filter automatically refreshes the category counts. Other viewer filters do not restrict category summaries. The standalone `Categories` component accepts optional `Date` and `TimeExpression` parameters; without either, summaries include all timestamps. Category edits retain these filters. These controls change persistence thresholds; upstream logging filters can still prevent messages from reaching ShambleLog. The demo references the Core project directly; other applications using the packaged Core dependency need an updated Core package for the startup seeding change. Category names are checked case-insensitively by the manager, but concurrent writes require a database uniqueness constraint to enforce this across processes.

Select a live category row to set the viewer's category-contains filter and return to the first page. Select a level count badge to set both the category-contains filter and the exact level filter. Other filters remain in effect. The standalone component exposes these selections through `CategorySelected` and `CategoryLevelSelected`, respectively.

The `EFCoreCriteria` overload requires `IEFCoreLoggingDbContext`; it returns command rows with their log entries and supports case-insensitive substring matching on individual table names, tags, and command text, plus minimum elapsed milliseconds. Matching lowercases both values; casing follows the query provider and runtime culture, so non-ASCII behavior can vary. Null, empty, or whitespace-only text filters do not filter. Pass optional `Criteria` to filter their log entries as well.

The FilterBar's rightmost **EF Core +** button expands a second row for these four command filters and switches the viewer to the `EFCoreCriteria` overload, retaining the normal log filters. With the row expanded and all command filters empty, only EF Core command logs are shown. **EF Core -** clears the command criteria, collapses the row, and restores the normal log query. Changing either set of filters returns to the first page. The standalone `FilterBar` supports two-way binding with `@bind-Criteria` and nullable `@bind-EFCoreCriteria`; a non-null EF Core criteria object expands the row. Contexts without `IEFCoreLoggingDbContext` display an error when EF Core filters are enabled.

Set `LogViewer`'s `EFCoreEnabled="true"` to show the EF Core sidebar accordion (off by default), and register `EFCoreMetricsQuery` with a logging context implementing `IEFCoreLoggingDbContext`. It groups metrics into Tags and Tables, showing count, min, max, average, and P95 elapsed milliseconds. Selecting a name expands the FilterBar's EF Core criteria and sets the matching tag or table substring filter, retaining all other filters and returning to the first page. Metrics refresh automatically when the viewer's date or time expression changes; other filters do not restrict the summaries. Refresh reloads metrics for the current date and time. The standalone `EFCore` component accepts `Queries`, optional `Date` and `TimeExpression`, and exposes selections through `MetricSelected` (`EFCoreMetricsQuery.Result`). Invalid expressions and unsupported contexts display an error in the helper.

## Demo database

Demo uses PostgreSQL with `ConnectionStrings:Local` in `Demo\appsettings.json`. Runtime registration provides a singleton `IDbContextFactory<ApplicationDbContext>`; dispose contexts created by the factory after use. The design-time factory reads the `appsettings.json` copied alongside the Demo assembly and requires the same connection-string key. It does not load environment-specific settings or environment-variable overrides.

With `dotnet-ef` version 11.0.0 installed, run from the solution directory:

```powershell
dotnet ef migrations add InitialCreate --project Demo --startup-project Demo --context ApplicationDbContext
dotnet ef database update --project Demo --startup-project Demo --context ApplicationDbContext
```

Creating migrations does not require a live database; applying them does. Keep database credentials out of source control.

## Publishing ShambleLog.Core

Add a NuGet.org API key authorized to push `ShambleLog.Core` as the repository Actions secret `NUGET_API_KEY`. Update the `Version` in `ShambleLog.Core\ShambleLog.Core.csproj` to an unpublished version and commit it. In GitHub Actions, manually run **Publish ShambleLog.Core** on the desired branch or tag. The workflow tests, packs, and publishes the version in that revision; publishing an existing version fails.

## Tests

`ShambleLog.Core.Tests` contains MSTest integration tests using isolated EF Core in-memory databases. Run them in Visual Studio Test Explorer or with `dotnet test ShambleLog.slnx`. The suite covers structured log data, filtering, concurrent writes and shutdown draining, duplicate registration, log entry middleware, recursion prevention, and database failure propagation.
