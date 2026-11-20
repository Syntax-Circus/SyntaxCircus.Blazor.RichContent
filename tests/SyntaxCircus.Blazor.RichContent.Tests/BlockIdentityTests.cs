using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Rendering;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class BlockIdentityTests
{
    private sealed class NoopBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private sealed class Resolver : IRichContentMediaResolver
    {
        public ResolvedMedia? Resolve(MediaRef media) => new("/media/" + media.Id);
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IErrorBoundaryLogger, NoopBoundaryLogger>()
            .AddSingleton<IRichContentMediaResolver, Resolver>()
            .AddRichContent()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private static RichTextBlock Rich(string id, string text) =>
        new(id, [new RichTextNode(RichTextNodeKind.Paragraph, Children: [new RichTextNode(RichTextNodeKind.Text, Text: text)])]);

    private static string TextOf(ContentBlock block) => ((RichTextBlock)block).Nodes[0].Children[0].Text!;

    private static ContentDocument Doc(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    private static ContentDocument TwoX() => Doc(Rich("x", "A"), Rich("x", "B"));

    [Fact]
    public async Task View_DuplicateIds_RendersFirstAndSkipsOnlyTheLater()
    {
        var html = await Render<ContentDocumentView>(new() { ["Document"] = Doc(Rich("intro", "Alpha"), Rich("intro", "Beta")) });

        html.ShouldContain("Alpha");
        html.ShouldNotContain("Beta");
    }

    [Fact]
    public async Task View_MalformedBlockSharingAnId_DoesNotDropTheValidBlock()
    {
        var malformed = new UnknownBlock("x", JsonDocument.Parse("""{"$block":"gallery","id":"x"}""").RootElement.Clone());

        var html = await Render<ContentDocumentView>(new() { ["Document"] = Doc(Rich("x", "Valid"), malformed) });

        html.ShouldContain("Valid");
    }

    [Fact]
    public void Replace_ByIndex_AffectsOnlyThatBlock()
    {
        var result = ContentDocumentOperations.Replace(TwoX(), 1, Rich("x", "B2"));

        TextOf(result.Blocks[0]).ShouldBe("A");
        TextOf(result.Blocks[1]).ShouldBe("B2");
    }

    [Fact]
    public void Remove_ByIndex_AffectsOnlyThatBlock()
    {
        var result = ContentDocumentOperations.Remove(TwoX(), 1);

        result.Blocks.Length.ShouldBe(1);
        TextOf(result.Blocks[0]).ShouldBe("A");
    }

    [Fact]
    public void Move_ByIndex_AffectsOnlyThatBlock()
    {
        var result = ContentDocumentOperations.Move(TwoX(), 1, -1);

        TextOf(result.Blocks[0]).ShouldBe("B");
        TextOf(result.Blocks[1]).ShouldBe("A");
    }

    [Fact]
    public void SplitRichText_ByIndex_SplitsTheIndexedBlock()
    {
        var two = new RichTextBlock("x", [
            new RichTextNode(RichTextNodeKind.Paragraph, Children: [new RichTextNode(RichTextNodeKind.Text, Text: "n1")]),
            new RichTextNode(RichTextNodeKind.Paragraph, Children: [new RichTextNode(RichTextNodeKind.Text, Text: "n2")])]);
        var doc = Doc(Rich("x", "A"), two);

        var result = ContentDocumentOperations.SplitRichText(doc, 1, 1, new CtaBlock("cta", "H", null, "Go", "/x"));

        result.Blocks.Length.ShouldBe(4);
        result.Blocks[0].ShouldBe(doc.Blocks[0]);
        result.Blocks[2].ShouldBeOfType<CtaBlock>();
    }

    [Fact]
    public void IndexOverloads_OutOfRange_LeaveDocumentUnchanged()
    {
        var doc = TwoX();

        ContentDocumentOperations.Replace(doc, 5, Rich("x", "z")).ShouldBeSameAs(doc);
        ContentDocumentOperations.Remove(doc, -1).ShouldBeSameAs(doc);
        ContentDocumentOperations.Move(doc, 9, 1).ShouldBeSameAs(doc);
    }

    [Fact]
    public async Task Editor_NullGalleryImageFromJson_RendersWithoutThrowing()
    {
        var doc = ContentDocumentSerializer.Deserialize("""{"schemaVersion":1,"blocks":[{"$block":"gallery","id":"g","images":[null]}]}""").Document!;

        var html = await Render<ContentDocumentEditor>(new() { ["Document"] = doc });

        html.ShouldContain("data-block-kind=\"gallery\"");
    }

    [Fact]
    public async Task Editor_NullChildFromJson_RendersWithoutThrowing()
    {
        var doc = ContentDocumentSerializer.Deserialize(
            """{"schemaVersion":1,"blocks":[{"$block":"rich-text","id":"r","nodes":[{"kind":"paragraph","children":[null]}]}]}""").Document!;

        var html = await Render<ContentDocumentEditor>(new() { ["Document"] = doc });

        html.ShouldContain("data-node-kind=\"Paragraph\"");
    }

    [Fact]
    public void ScaffoldDepth_IsTheDepthOfTheCreatedTree()
    {
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.Text).ShouldBe(1);
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.Paragraph).ShouldBe(2);
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.Link).ShouldBe(2);
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.ListItem).ShouldBe(3);
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.List).ShouldBe(4);
        RichTextEditorOperations.ScaffoldDepth(RichTextNodeKind.Table).ShouldBe(4);
    }

    [Fact]
    public async Task NodeEditor_NearMaxDepth_OffersOnlyKindsThatFit()
    {
        var paragraph = new RichTextNode(RichTextNodeKind.Paragraph, Children: [new RichTextNode(RichTextNodeKind.Text, Text: "x")]);

        var html = await Render<RichTextNodeEditor>(new() { ["Node"] = paragraph, ["Depth"] = 7, ["MaxDepth"] = 8 });

        html.ShouldContain("Add text");
        html.ShouldNotContain("Add link");
        html.ShouldNotContain("Add italic");
        html.ShouldNotContain("Add bold");
    }
}
