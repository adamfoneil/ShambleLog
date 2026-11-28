# ShambleLog

When I was working on my Blazor Template, I wanted to integrate a log provider with a really nice consume experience, since that is what I feel is missing in the Serilog ecosystem. Yes, you can use Seq or Graylog or something along those lines. But those don't have the experience I'm looking for, and they run as standalone apps. I wanted to integrate a log viewer as a Razor Class Library for truly seamless integration with consuming apps.

The other motivation is that I don't really like Serilog's relational database integration. It is a bit too opinionated and not flexible in the ways I want it to be. I wanted a more native EF Core experience, with a DbContext and migrations, so that I can use the same database provider as my app and have a more natural integration with my existing data model.

I made [Bramblelog](https://bramblelog.dev) as a cloud-native logging provider with the consume experience I was after, and to avoid having to configure log storage locally. But I felt that a cloud SaaS dependency was too heavy for a template, and a hard sell to begin with. So I made ShambleLog, a local logging provider that can be used with any .NET application.

ShambleLog is an `ILogger<T>` provider that writes structured logs to an EF Core context in the background. The optional Blazor viewer lets you search logs and manage category levels inside your application. The [Demo](https://github.com/adamfoneil/ShambleLog/tree/main/Demo) shows the pieces working together with PostgreSQL.

## Set up logging with PostgreSQL

The current source targets .NET 11 and EF Core 11. Reference `ShambleLog.Core` and `ShambleLog.PostgreSQL`, either as compatible NuGet packages or as projects. The PostgreSQL project provides entity configurations and a query implementation, not a preconfigured context. Preview packages may require the `--prerelease` option when installing. The example below follows the Demo and enables optional EF Core command tracking.

### 1. Add the entities to your context

```csharp
using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.PostgreSQL.Configurations;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IEFCoreLoggingDbContext
{
    public DbSet<LogEntry> LogEntries { get; set; } = null!;
    public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;
    public DbSet<EFCoreEntry> EFCoreEntries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new LogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryLevelConfiguration());
        modelBuilder.ApplyConfiguration(new EFCoreEntryConfiguration());
    }
}
```

If you do not need command tracking, implement `ILoggingDbContext` from `ShambleLog.Core` instead, and omit `EFCoreEntries` and its configuration. The PostgreSQL configuration stores structured properties as `jsonb`. Other EF Core providers can use their own mappings; see the [reference](https://github.com/adamfoneil/ShambleLog/blob/main/REFERENCE.md#using-another-database-provider).

### 2. Register the context and provider

In `Program.cs`, configure the connection string through your normal configuration sources, and register the context before starting the host:

```csharp
using Microsoft.EntityFrameworkCore;
using ShambleLog.Core;
using ShambleLog.Core.Middleware;

var connectionString = builder.Configuration.GetConnectionString("Local")
    ?? throw new InvalidOperationException("Connection string 'Local' is required.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Logging.AddShambleLog<ApplicationDbContext>(options =>
    options.TrackEFCoreQueries());
```

Omit the callback if you are not tracking EF Core commands. The Demo instead uses `AddDbContextFactory` and enables command tracking. For command entries to be recorded, `Microsoft.EntityFrameworkCore.Database.Command` at `Information` must pass the applicable filters and category rules.

### 3. Create the tables and write logs

Create and apply an EF Core migration for your application context **before starting the host**. ShambleLog initializes category rules at startup and requires the logging tables to exist. For example, with the appropriate design-time context configuration and a `dotnet-ef` version matching your EF Core packages:

```powershell
dotnet ef migrations add AddShambleLog --project YourApp --context ApplicationDbContext
dotnet ef database update --project YourApp --context ApplicationDbContext
```

Use your existing migrations instead if the logging entities are already included. Keep credentials out of source control. Once the host is running, use the standard logging API:

```csharp
logger.LogInformation("Processed order {OrderId}", orderId);
```

Category levels are seeded from `Logging:LogLevel` on startup, without overwriting existing database rules. The longest matching category prefix determines the database minimum level; upstream provider filters may also discard logs. The queue is in memory, not durable, and database failures can stop the background writer. See the [reference](https://github.com/adamfoneil/ShambleLog/blob/main/REFERENCE.md) for filtering, retention, middleware, and operational details. Avoid logging sensitive data.

### Optional features

Configure these on the same `AddShambleLog` call when needed:

```csharp
builder.Logging.AddShambleLog<ApplicationDbContext>(options => options
    .TrackEFCoreQueries()
    .WithRetentionPeriod(new RetentionPeriodOptions
    {
        DeleteAfter = TimeSpan.FromDays(30),
        Interval = TimeSpan.FromHours(1)
    })
    .WithHttpRequestLogging()
    .WithIdentityUser());
```

Retention, HTTP request logging, and identity capture are opt-in. The HTTP features apply to ASP.NET Core applications; request paths and user names may contain sensitive data. See the [reference](https://github.com/adamfoneil/ShambleLog/blob/main/REFERENCE.md) for their behavior and privacy considerations.

## Add the Blazor viewer

The viewer is optional. The current `ShambleLog.RCL` is a Razor class library project in this repository; the Demo references it directly. Reference that project in your Blazor app along with the Core and PostgreSQL dependencies. Register Radzen and the viewer's query services, as in the Demo:

```csharp
using Radzen;
using ShambleLog.Core.Queries;
using ShambleLog.PostgreSQL;

builder.Services.AddRadzenComponents();
builder.Services.AddTransient<LogQuery, PostgreSQLLogQuery>();
builder.Services.AddTransient<CategorySummaryQuery>();
builder.Services.AddTransient<CategoryLevelQuery>();
builder.Services.AddTransient<ScopedQueryRunner>();
builder.Services.AddTransient<EFCoreMetricsQuery>(); // For EF Core metrics.
```

Add a Radzen theme and JavaScript to the app's HTML shell, and include `<RadzenContextMenu />` in the interactive layout. See [Demo's App.razor](https://github.com/adamfoneil/ShambleLog/blob/main/Demo/Components/App.razor) and [MainLayout.razor](https://github.com/adamfoneil/ShambleLog/blob/main/Demo/Components/Layout/MainLayout.razor) for the asset and layout setup. Ensure the viewer runs interactively (the Demo uses interactive server rendering) and protect the page with appropriate authorization: it can expose logs and change persistence levels.

Add the viewer to a page:

```razor
@page "/logs"
@using ShambleLog.Core.Queries
@using ShambleLog.RCL
@inject CategoryLevelQuery CategoryLevelQuery
@inject ScopedQueryRunner Queries

<LogViewer CategoryLevelQuery="CategoryLevelQuery" Queries="Queries" EFCoreEnabled="true" />
```

`EFCoreEnabled` shows the EF Core metrics sidebar; use it with command tracking and `IEFCoreLoggingDbContext` as configured above. Otherwise omit the parameter and `EFCoreMetricsQuery` registration. The viewer can filter logs by date, category, level, message, properties, and EF Core command details.

## Run the Demo

Configure `ConnectionStrings:Local` for a reachable PostgreSQL database. The Demo's design-time context factory reads `Demo\appsettings.json` from the built output, not environment-specific configuration or environment variables; do not commit credentials. With `dotnet-ef` 11.0.0 installed, run from the repository root:

```powershell
dotnet ef migrations add InitialCreate --project Demo --startup-project Demo --context ApplicationDbContext
dotnet ef database update --project Demo --startup-project Demo --context ApplicationDbContext
dotnet run --project Demo
```

Creating a migration does not require a live database; applying it does. If you have already created the migration, skip the first command.

## More information

See the [detailed reference](https://github.com/adamfoneil/ShambleLog/blob/main/REFERENCE.md) for custom providers, retention, HTTP logging, middleware, query syntax, category management, and writer behavior.
