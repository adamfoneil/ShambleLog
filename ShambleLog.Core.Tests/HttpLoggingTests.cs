using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class HttpLoggingTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task RequestLogging_CapturesIdentityAndHandledStatusWithoutSensitiveRequestData()
    {
        using var host = CreateHost(true, true);
        await host.StartAsync(testContext.CancellationToken);
        using var client = CreateClient(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/failure?secret=query-value")
        {
            Content = new StringContent("body-value")
        };
        request.Headers.Add("X-Secret", "header-value");
        using var response = await client.SendAsync(request, testContext.CancellationToken);
        Assert.AreEqual(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("body-value", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
        host.Services.GetRequiredService<ILogger<HttpLoggingTests>>().LogWarning("Outside request");
        await host.StopAsync(testContext.CancellationToken);

        var entries = await ReadEntriesAsync(host);
        var before = entries.Single(entry => entry.Message == "Before authentication");
        var during = entries.Single(entry => entry.Message == "During request");
        var completion = entries.Single(entry => entry.Template.StartsWith("HTTP {Method}"));
        Assert.IsFalse(before.Properties.ContainsKey("IdentityUser"));
        Assert.AreEqual("alice", Property(during, "IdentityUser").GetString());
        Assert.AreEqual("bob", Property(completion, "IdentityUser").GetString());
        Assert.IsFalse(entries.Single(entry => entry.Message == "Outside request").Properties.ContainsKey("IdentityUser"));
        Assert.AreEqual("POST", Property(completion, "Method").GetString());
        Assert.AreEqual("/failure", Property(completion, "Path").GetString());
        Assert.AreEqual(409, Property(completion, "StatusCode").GetInt32());
        Assert.IsGreaterThanOrEqualTo(0, Property(completion, "DurationMs").GetDouble());
        var logged = JsonSerializer.Serialize(completion);
        Assert.IsFalse(logged.Contains("query-value") || logged.Contains("header-value") || logged.Contains("body-value"));
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task RequestLogging_AnonymousUnhandledExceptionAndOptOut()
    {
        using (var host = CreateHost(true, true))
        {
            await host.StartAsync(testContext.CancellationToken);
            using var client = CreateClient(host);
            using var response = await client.GetAsync("/unhandled", testContext.CancellationToken);
            Assert.AreEqual(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            using var anonymous = await client.GetAsync("/anonymous", testContext.CancellationToken);
            await host.StopAsync(testContext.CancellationToken);
            var entries = await ReadEntriesAsync(host);
            var completion = entries.Single(entry => entry.Template.StartsWith("HTTP {Method}") && Property(entry, "Path").GetString() == "/unhandled");
            Assert.AreEqual(500, Property(completion, "StatusCode").GetInt32());
            Assert.IsFalse(completion.Properties.ContainsKey("IdentityUser"));
            Assert.IsFalse(entries.Single(entry => entry.Message == "Anonymous request").Properties.ContainsKey("IdentityUser"));
        }

        using (var host = CreateHost(false, false))
        {
            await host.StartAsync(testContext.CancellationToken);
            using var client = CreateClient(host);
            using var response = await client.GetAsync("/ok", testContext.CancellationToken);
            await host.StopAsync(testContext.CancellationToken);
            var entries = await ReadEntriesAsync(host);
            Assert.IsFalse(entries.Any(entry => entry.Template.StartsWith("HTTP {Method}")));
            Assert.IsFalse(entries.Single(entry => entry.Message == "During request").Properties.ContainsKey("IdentityUser"));
        }
    }

    private static IHost CreateHost(bool identity, bool requests)
    {
        var databaseName = Guid.NewGuid().ToString();
        return new HostBuilder()
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddShambleLog<TestDbContext>(shamble =>
            {
                if (identity) shamble.WithIdentityUser();
                if (requests) shamble.WithHttpRequestLogging().WithHttpRequestLogging();
            });
        })
        .ConfigureServices(services =>
        {
            services.AddDbContext<TestDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        })
        .ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0").Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/unhandled")
                    throw new InvalidOperationException("Unhandled");
                await next(context);
            });
            app.UseExceptionHandler(error => error.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync((string)context.Items["body"]!);
            }));
            app.Use(async (context, next) =>
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<HttpLoggingTests>>();
                logger.LogWarning("Before authentication");
                await next(context);
            });
            app.UseAuthentication();
            app.Run(async context =>
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<HttpLoggingTests>>();
                if (context.Request.Path == "/anonymous")
                {
                    logger.LogWarning("Anonymous request");
                    return;
                }
                logger.LogWarning("During request");
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob")], "test"));
                if (context.Request.Path == "/failure")
                {
                    using var reader = new StreamReader(context.Request.Body);
                    context.Items["body"] = await reader.ReadToEndAsync();
                    throw new InvalidOperationException("Handled");
                }
                await context.Response.WriteAsync("ok");
            });
        }))
        .Build();
    }

    private static HttpClient CreateClient(IHost host) => new()
    {
        BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private async Task<List<LogEntry>> ReadEntriesAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().LogEntries
            .AsNoTracking().ToListAsync(testContext.CancellationToken);
    }

    private static JsonElement Property(LogEntry entry, string name) => (JsonElement)entry.Properties[name]!;

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Path == "/anonymous")
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), ILoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => JsonSerializer.Serialize(properties, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, (JsonSerializerOptions?)null)!);
    }
}
