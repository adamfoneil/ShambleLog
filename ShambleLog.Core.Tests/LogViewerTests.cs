using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.Core.Queries;
using ShambleLog.RCL;

#pragma warning disable BL0006

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class LogViewerTests
{
    [TestMethod]
    public async Task PropertyFilter_ShowsErrorAndRecoversAfterCorrection()
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.LogEntries.Add(new LogEntry
        {
            Id = 1, Timestamp = DateTime.UtcNow, Category = "Example", Message = "matching row",
            Properties = new() { ["UserId"] = "123" }
        });
        await db.SaveChangesAsync();
        await using var renderer = new ComponentRenderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = await renderer.RenderAsync(db);
            Assert.IsTrue(renderer.Text(id).Contains("matching row"));
            await renderer.ChangeAsync(id, "properties: UserId:123", "UserId:");
            Assert.IsTrue(renderer.Text(id).Contains("Use comma-separated property:value pairs"));
            Assert.IsTrue(renderer.Text(id).Contains("matching row"));
            await renderer.ChangeAsync(id, "properties: UserId:123", "UserId:missing");
            Assert.IsFalse(renderer.Text(id).Contains("Use comma-separated property:value pairs"));
            Assert.IsFalse(renderer.Text(id).Contains("matching row"));
            await renderer.ChangeAsync(id, "properties: UserId:123", "UserId:123");
            Assert.IsTrue(renderer.Text(id).Contains("matching row"));
        });
    }

    private sealed class ComponentRenderer : Renderer
    {
        public ComponentRenderer() : base(CreateServices(), LoggerFactory.Create(_ => { })) { }
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        private static IServiceProvider CreateServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRadzenComponents();
            services.AddSingleton<IJSRuntime, StubJsRuntime>();
            return services.BuildServiceProvider();
        }

        public async Task<int> RenderAsync(TestDbContext db)
        {
            var id = AssignRootComponentId(new LogViewer());
            await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["LogQuery"] = new ClientSideLogQuery(db),
                ["CategorySummaryQuery"] = new CategorySummaryQuery(db),
                ["EFCoreSummaryQuery"] = new EFCoreSummaryQuery(db)
            }));
            return id;
        }

        public string Text(int id) => string.Join(" ", Frames(id).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => null
        }));

        public async Task ChangeAsync(int id, string placeholder, string value)
        {
            var frames = Frames(id).ToArray();
            for (var i = 0; i < frames.Length; i++)
            {
                if (frames[i].FrameType != RenderTreeFrameType.Element || frames[i].ElementName != "input")
                    continue;
                var attributes = frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (attributes.Any(frame => frame.AttributeName == "placeholder" && Equals(frame.AttributeValue, placeholder)))
                {
                    var handler = attributes.Single(frame => frame.AttributeName == "onchange").AttributeEventHandlerId;
                    await DispatchEventAsync(handler, null, new ChangeEventArgs { Value = value });
                    return;
                }
            }
            Assert.Fail($"Input not found: {placeholder}");
        }

        private IEnumerable<RenderTreeFrame> Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            foreach (var frame in frames.Array[..frames.Count])
            {
                yield return frame;
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var child in Frames(frame.ComponentId))
                        yield return child;
            }
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IEFCoreLoggingDbContext
    {
        public DbSet<LogEntry> LogEntries { get; set; } = null!;
        public DbSet<CategoryLevel> LogCategoryLevels { get; set; } = null!;
        public DbSet<EFCoreEntry> EFCoreEntries { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LogEntry>().Property(entry => entry.Properties).HasConversion(
                properties => System.Text.Json.JsonSerializer.Serialize(properties, (System.Text.Json.JsonSerializerOptions?)null),
                json => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json, (System.Text.Json.JsonSerializerOptions?)null)!);
            modelBuilder.Entity<EFCoreEntry>().HasKey(entry => entry.LogEntryId);
            modelBuilder.Entity<EFCoreEntry>().HasOne(entry => entry.LogEntry).WithOne(entry => entry.EFCoreEntry)
                .HasForeignKey<EFCoreEntry>(entry => entry.LogEntryId);
        }
    }
}
