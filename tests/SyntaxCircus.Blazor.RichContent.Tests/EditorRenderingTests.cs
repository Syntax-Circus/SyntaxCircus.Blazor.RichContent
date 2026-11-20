using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Rendering;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed partial class EditorRenderingTests
{
    private sealed class Resolver : IRichContentMediaResolver
    {
        public ResolvedMedia? Resolve(MediaRef media) => new("/media/" + media.Id);
    }

    private sealed class NoopBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    public sealed class FakePicker : MediaPickerBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "fake-picker");
            builder.AddContent(2, Value?.Id ?? "none");
            builder.CloseElement();
        }
    }

    public sealed class CardView : ComponentBase
    {
        [Parameter] public JsonElement Data { get; set; }
    }

    public sealed class ExplodingView : ComponentBase
    {
        [Parameter] public JsonElement Data { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("boom");
    }

    private static ServiceProvider Services(Action<RichContentEditorOptions>? editor = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IErrorBoundaryLogger, NoopBoundaryLogger>()
            .AddSingleton<IRichContentMediaResolver, Resolver>()
            .AddRichContent(blocks => blocks.Add("project-card", "Project card", typeof(CardView)), editor)
            .BuildServiceProvider();

    private static async Task<string> RenderEditor(ContentDocument document, Action<RichContentEditorOptions>? editor = null)
    {
        using var services = Services(editor);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ContentDocumentEditor>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Document"] = document }))).ToHtmlString());
    }

    private static ContentDocument Doc(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    // Finds the first <button> with the given visible text and reports whether it carries the disabled attribute.
    private static bool? ButtonDisabled(string html, string text)
    {
        var match = Regex.Match(html, @"<button([^>]*)>\s*" + Regex.Escape(text) + @"\s*</button>");
        return match.Success ? Regex.IsMatch(match.Groups[1].Value, @"\bdisabled\b") : null;
    }

    [Fact]
    public async Task Editor_EmptyDocument_ShowsOneRichTextBlock()
    {
        var html = await RenderEditor(ContentDocument.Empty("first"));

        Regex.Count(html, "data-block-kind=\"rich-text\"").ShouldBe(1);
        html.ShouldContain("aria-label=\"Post body\"");
    }

    [Fact]
    public async Task Editor_UnknownBlock_RendersPreservedCard()
    {
        var raw = JsonDocument.Parse("""{"$block":"future-thing","id":"u1"}""").RootElement.Clone();

        var html = await RenderEditor(Doc(new UnknownBlock("u1", raw)));

        html.ShouldContain("unsupported block (kept as-is)");
        html.ShouldContain("data-block-kind=\"unknown\"");
    }

    [Fact]
    public async Task Editor_NoMediaPicker_DisablesImageAndGalleryAdds()
    {
        var html = await RenderEditor(ContentDocument.Empty("first"));

        ButtonDisabled(html, "Add image").ShouldBe(true);
        ButtonDisabled(html, "Gallery").ShouldBe(true);
        html.ShouldContain("No media picker is configured");
    }

    [Fact]
    public async Task Editor_WithMediaPicker_EnablesImageAndGalleryAdds()
    {
        var html = await RenderEditor(ContentDocument.Empty("first"), o => o.MediaPicker = typeof(FakePicker));

        ButtonDisabled(html, "Add image").ShouldBe(false);
        ButtonDisabled(html, "Gallery").ShouldBe(false);
    }

    [Fact]
    public async Task Editor_RegisteredExtension_AppearsInAddMenu()
    {
        var html = await RenderEditor(ContentDocument.Empty("first"));

        ButtonDisabled(html, "Project card").ShouldBe(false);
    }

    [Fact]
    public async Task Editor_InvalidBlock_ShowsErrorNextToBlock()
    {
        var html = await RenderEditor(Doc(new CtaBlock("cta-1", "Heading", null, "Go", "javascript:alert(1)")));

        var block = Regex.Match(html, "<section[^>]*data-block-id=\"cta-1\".*?</section>", RegexOptions.Singleline).Value;
        block.ShouldContain("role=\"alert\"");
        block.ShouldContain("The call to action URL is not allowed.");
    }

    [Fact]
    public async Task Editor_CodeBlock_UsesLanguageSelectAndMonospaceTextarea()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "x", Language: "csharp");

        var html = await RenderEditor(Doc(new RichTextBlock("a", [code])));

        html.ShouldContain("<select");
        html.ShouldContain("<option value=\"csharp\"");
        html.ShouldContain("spellcheck=\"false\"");
        html.ShouldContain("class=\"code\"");
    }

    [Fact]
    public async Task Editor_ErrorsChanged_ReportsValidationErrors()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        IReadOnlyList<RichContentError>? reported = null;
        var callback = EventCallback.Factory.Create<IReadOnlyList<RichContentError>>(this, errors => reported = errors);

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ContentDocumentEditor>(ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                ["Document"] = Doc(new CtaBlock("cta-1", "H", null, "", "javascript:alert(1)")),
                ["ErrorsChanged"] = callback,
            })));

        reported.ShouldNotBeNull();
        reported.Select(e => e.Code).ShouldBe(["cta-label-required", "unsafe-url"], ignoreOrder: true);
    }

    [Fact]
    public void EditorOptions_MediaPickerMustDeriveFromMediaPickerBase()
    {
        Should.Throw<ArgumentException>(() => new RichContentEditorOptions { MediaPicker = typeof(CardView) });
        new RichContentEditorOptions { MediaPicker = typeof(FakePicker) }.MediaPicker.ShouldBe(typeof(FakePicker));
    }

    [Fact]
    public async Task Editor_DuplicateBlockIds_RendersWithoutThrowing()
    {
        var para = new RichTextNode(RichTextNodeKind.Paragraph);

        var html = await RenderEditor(Doc(new RichTextBlock("same", [para]), new RichTextBlock("same", [para])));

        Regex.Count(html, "data-block-id=\"same\"").ShouldBe(2);
        html.ShouldContain("used more than once");
    }

    [Fact]
    public async Task Editor_TableWithDefaultChildren_RendersWithoutThrowing()
    {
        var table = new RichTextNode(RichTextNodeKind.Table, Children: [new(RichTextNodeKind.TableRow), new(RichTextNodeKind.TableRow)]);

        var html = await RenderEditor(Doc(new RichTextBlock("t", [table])));

        html.ShouldContain("Add row");
        ButtonDisabled(html, "Remove last column").ShouldBe(true);
    }

    [Fact]
    public async Task AddRichContent_WithoutAspNetCoreHost_ExtensionRendererFailureDoesNotThrow()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IRichContentMediaResolver, Resolver>()
            .AddRichContent(blocks => blocks.Add("exploding", "Exploding", typeof(ExplodingView)))
            .BuildServiceProvider();
        var doc = Doc(
            new ExtensionBlock("x1", "exploding", JsonDocument.Parse("{}").RootElement.Clone()),
            new RichTextBlock("t1", [new RichTextNode(RichTextNodeKind.Paragraph, Children: [new RichTextNode(RichTextNodeKind.Text, Text: "still here")])]));

        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ContentDocumentView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Document"] = doc }))).ToHtmlString());

        html.ShouldContain("still here");
    }
}
