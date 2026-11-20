using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed partial class EditorRenderingTests
{
    public sealed class ExplodingEditor : ExtensionBlockEditorBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("boom");
    }

    public sealed class ExplodingPicker : MediaPickerBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("boom");
    }

    private static async Task<string> RenderWith(ServiceProvider services, Dictionary<string, object?> parameters)
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ContentDocumentEditor>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    [Fact]
    public async Task Editor_ThrowingExtensionEditor_ShowsMessage_AndRestOfEditorRenders()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IErrorBoundaryLogger, NoopBoundaryLogger>()
            .AddSingleton<IRichContentMediaResolver, Resolver>()
            .AddRichContent(blocks => blocks.Add("boom-card", "Boom", typeof(CardView), typeof(ExplodingEditor)))
            .BuildServiceProvider();
        var doc = Doc(
            new ExtensionBlock("x1", "boom-card", JsonDocument.Parse("{}").RootElement.Clone()),
            new RichTextBlock("t1", [new RichTextNode(RichTextNodeKind.Paragraph)]));

        var html = await RenderWith(services, new() { ["Document"] = doc });

        html.ShouldContain("This block's editor failed to load; the block is kept unchanged.");
        html.ShouldContain("data-block-id=\"t1\"");
    }

    [Fact]
    public async Task Editor_ThrowingMediaPicker_ShowsMessage_AndRestOfEditorRenders()
    {
        var image = new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("m1"));

        var html = await RenderEditor(Doc(new RichTextBlock("t1", [image, new RichTextNode(RichTextNodeKind.Paragraph)])), o => o.MediaPicker = typeof(ExplodingPicker));

        html.ShouldContain("kept unchanged.");
        html.ShouldContain("Add paragraph");
    }

    [Fact]
    public async Task Editor_NullDocument_RendersNothing()
    {
        using var services = Services();

        var html = await RenderWith(services, new() { ["Document"] = null });

        html.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task Editor_CalloutEditor_HonoursConfiguredDepthLimit()
    {
        using var services = Services();
        var doc = Doc(new CalloutBlock("c1", CalloutTone.Info, null, [new RichTextNode(RichTextNodeKind.Paragraph)]));

        var html = await RenderWith(services, new() { ["Document"] = doc, ["Limits"] = new RichContentLimits(Depth: 1) });

        html.ShouldContain("Maximum formatting depth reached.");
    }

    [Fact]
    public async Task Editor_UnlistedLanguage_IsSelectableAndShownAsIs()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "x", Language: "zig");

        var html = await RenderEditor(Doc(new RichTextBlock("a", [code])));

        Regex.IsMatch(html, "<option value=\"zig\"[^>]*>zig</option>").ShouldBeTrue();
        html.ShouldNotContain("value=\"json\"");
    }

    public sealed class RenderProbePicker : MediaPickerBase
    {
        public static readonly ConcurrentDictionary<string, int> Renders = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            Renders.AddOrUpdate(Value?.Id ?? "none", 1, (_, n) => n + 1);
    }

    public sealed class Holder
    {
        public NodeHost? Host { get; set; }
    }

    public sealed class NodeHost : ComponentBase
    {
        [Parameter] public Holder Handle { get; set; } = default!;

        public RichTextNode First { get; private set; } = new(RichTextNodeKind.Image, Media: new MediaRef("probe-a"), Alt: "one");

        public RichTextNode Second { get; private set; } = new(RichTextNodeKind.Image, Media: new MediaRef("probe-b"), Alt: "two");

        protected override void OnInitialized() => Handle.Host = this;

        public Task EditFirst() => InvokeAsync(() =>
        {
            First = First with { Alt = "changed" };
            StateHasChanged();
        });

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RichTextNodeEditor>(0);
            builder.AddComponentParameter(1, nameof(RichTextNodeEditor.Node), First);
            builder.CloseComponent();
            builder.OpenComponent<RichTextNodeEditor>(2);
            builder.AddComponentParameter(3, nameof(RichTextNodeEditor.Node), Second);
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task NodeEditor_EditingOneNode_DoesNotReRenderUnchangedSibling()
    {
        RenderProbePicker.Renders.Clear();
        using var services = Services(o => o.MediaPicker = typeof(RenderProbePicker));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var handle = new Holder();
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<NodeHost>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Handle"] = handle })));
        RenderProbePicker.Renders["probe-a"].ShouldBe(1);
        RenderProbePicker.Renders["probe-b"].ShouldBe(1);

        await handle.Host!.EditFirst();

        RenderProbePicker.Renders["probe-a"].ShouldBe(2);
        RenderProbePicker.Renders["probe-b"].ShouldBe(1);
    }
}
