using Demo;
using Demo.Components;
using Microsoft.EntityFrameworkCore;
using Radzen;
using ShambleLog.Core;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;
using ShambleLog.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddShambleLog<ApplicationDbContext>(options => options.TrackEFCoreQueries());

// Radzen requirement
builder.Services.AddRadzenComponents();

// needed for RCL UI
builder.Services.AddTransient<LogQuery, PostgreSQLLogQuery>();
builder.Services.AddTransient<CategorySummaryQuery>();
builder.Services.AddTransient<EFCoreSummaryQuery>();

var connectionString = builder.Configuration.GetConnectionString("Local");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'Local' is required.");
}

// Add services to the container.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString), ServiceLifetime.Singleton);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
