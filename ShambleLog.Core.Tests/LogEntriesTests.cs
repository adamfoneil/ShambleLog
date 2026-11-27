using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using ShambleLog.Core.Entities;
using ShambleLog.RCL;

#pragma warning disable BL0006

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class LogEntriesTests
{
    [TestMethod]
    public async Task RowsExpandAndPropertiesCanBePinnedAndUnpinned()
    {
        await using var renderer = new ComponentRenderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = await renderer.RenderAsync([
                new LogEntry { Id = 1, Category = "Example", Level = LogLevel.Warning, Message = "First", Properties = new() { ["RequestId"] = "abc", ["Items"] = new[] { "one", "two" } } },
                new LogEntry { Id = 2, Message = "Second", Properties = new() { ["RequestId"] = "def" } }
            ]);

            Assert.IsTrue(renderer.Text(id).Contains("Example"));
            Assert.IsFalse(renderer.Text(id).Contains("abc"));
            await renderer.ClickAsync(id, "Expand");
            Assert.IsTrue(renderer.Text(id).Contains("abc"));
            Assert.IsTrue(renderer.Text(id).Contains("[\"one\",\"two\"]"));
            await renderer.ClickAsync(id, "Pin RequestId");
            Assert.IsTrue(renderer.Text(id).Contains("def"));
            Assert.IsTrue(renderer.Attributes(id).Contains("repeat(1, minmax(120px, 1fr))"));
            await renderer.ClickAsync(id, "Remove RequestId column");
            Assert.IsFalse(renderer.Text(id).Contains("def"));
            await renderer.ClickAsync(id, "Collapse");
            Assert.IsFalse(renderer.Text(id).Contains("abc"));
        });
    }

    [TestMethod]
    public async Task DisplaysAvailableEfCoreDetailsAndEmptyDetails()
    {
        await using var renderer = new ComponentRenderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = await renderer.RenderAsync([
                new LogEntry { Id = 1, EFCoreEntry = new() { TargetTables = ["Users"], Tags = ["report"], Parameters = "@p=1", CommandText = "select 1", ElapsedMs = 42 } },
                new LogEntry { Id = 2, Message = "plain" }
            ]);
            var text = renderer.Text(id);
            foreach (var expected in new[] { "Elapsed (ms)", "Users", "report", "@p=1", "select 1", "42", "plain" })
                Assert.IsTrue(text.Contains(expected), expected);
            await renderer.ClickAsync(id, "Expand");
            Assert.IsTrue(renderer.Text(id).Contains("No additional details."));
            await renderer.RenderAsync([], id);
            Assert.IsFalse(renderer.Text(id).Contains("Elapsed (ms)"));
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

        public async Task<int> RenderAsync(LogEntry[] entries, int? id = null)
        {
            var componentId = id ?? AssignRootComponentId(new LogEntries());
            await RenderRootComponentAsync(componentId, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = entries }));
            return componentId;
        }

        public string Text(int id) => string.Join(" ", Frames(id).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => null
        }));

        public string Attributes(int id) => string.Join(" ", Frames(id).Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).Select(frame => frame.AttributeValue));

        public async Task ClickAsync(int id, string label)
        {
            var frames = Frames(id);
            for (var i = 0; i < frames.Length; i++)
            {
                if (frames[i].FrameType != RenderTreeFrameType.Element || frames[i].ElementName != "button")
                    continue;
                var attributes = frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (attributes.Any(frame => frame.AttributeName == "aria-label" && Equals(frame.AttributeValue, label)))
                {
                    var handler = attributes.Single(frame => frame.AttributeName == "onclick").AttributeEventHandlerId;
                    await DispatchEventAsync(handler, null, new MouseEventArgs());
                    return;
                }
            }
            Assert.Fail($"Button not found: {label}");
        }

        private RenderTreeFrame[] Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array[..frames.Count];
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
