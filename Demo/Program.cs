using Demo;
using Demo.Components;
using Microsoft.EntityFrameworkCore;
using Radzen;
using ShambleLog.Core;
using ShambleLog.Core.Middleware;
using ShambleLog.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddShambleLog<ApplicationDbContext, PostgreSQLLogQuery>(options => options.TrackEFCoreQueries());

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

var connectionString = builder.Configuration.GetConnectionString("Local");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'Local' is required.");
}

builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString), ServiceLifetime.Singleton);

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
