using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.Rendering;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class RenderingTests
{
    private sealed class Resolver(bool resolves = true) : IRichContentMediaResolver
    {
        public ResolvedMedia? Resolve(MediaRef media) =>
            resolves ? new ResolvedMedia("/media/" + media.Id, "/media/" + media.Id + "-2x 2x", "100vw", 640, 480) : null;
    }

    public sealed class ProjectCard : ComponentBase
    {
        [Parameter] public JsonElement Data { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "project-card");
            builder.AddContent(2, Data.GetProperty("name").GetString());
            builder.CloseElement();
        }
    }

    public sealed class ExplodingCard : ComponentBase
    {
        [Parameter] public JsonElement Data { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("boom");
    }

    // A real app gets IErrorBoundaryLogger from AddRazorComponents; the bare test host must supply it.
    private sealed class TestErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private static ServiceProvider Services(bool resolves = true) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IErrorBoundaryLogger, TestErrorBoundaryLogger>()
            .AddSingleton<IRichContentMediaResolver>(new Resolver(resolves))
            .AddRichContent(blocks => blocks.Add("project-card", "Project card", typeof(ProjectCard)).Add("exploding", "Exploding", typeof(ExplodingCard)))
            .BuildServiceProvider();

    private static async Task<string> Render<TComponent>(Dictionary<string, object?> parameters, bool resolves = true)
        where TComponent : IComponent
    {
        using var services = Services(resolves);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private static Task<string> RenderDocument(ContentDocument document, bool resolves = true) =>
        Render<ContentDocumentView>(new() { ["Document"] = document }, resolves);

    private static ContentDocument Doc(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    private static RichTextNode Text(string text) => new(RichTextNodeKind.Text, Text: text);

    private static RichTextNode Para(params RichTextNode[] children) => new(RichTextNodeKind.Paragraph, Children: [.. children]);

    private static RichTextNode Heading(int level, string text) => new(RichTextNodeKind.Heading, HeadingLevel: level, Children: [Text(text)]);

    private static RichTextBlock Rich(string id, params RichTextNode[] nodes) => new(id, [.. nodes]);

    private static RichTextNode Cell(string text, bool header) => new(RichTextNodeKind.TableCell, Header: header, Children: [Text(text)]);

    [Fact]
    public async Task RendersEveryNodeKindWithExpectedTags()
    {
        var item = new RichTextNode(RichTextNodeKind.ListItem, Children: [Text("one")]);
        var doc = Doc(
            Rich("a",
                Heading(2, "Two"), Heading(3, "Three"), Heading(4, "Four"),
                Para(
                    Text("plain "),
                    new RichTextNode(RichTextNodeKind.Emphasis, Children: [Text("emph")]),
                    new RichTextNode(RichTextNodeKind.Strong, Children: [Text("strong")]),
                    new RichTextNode(RichTextNodeKind.InlineCode, Text: "inline"),
                    new RichTextNode(RichTextNodeKind.Link, Url: "/about", Children: [Text("about")])),
                new RichTextNode(RichTextNodeKind.List, Ordered: false, Children: [item]),
                new RichTextNode(RichTextNodeKind.List, Ordered: true, Children: [item]),
                new RichTextNode(RichTextNodeKind.Quote, Children: [Para(Text("quoted"))]),
                new RichTextNode(RichTextNodeKind.CodeBlock, Text: "x", Language: "brainfuck"),
                new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("m1"), Alt: "An alt", Caption: "A caption"),
                new RichTextNode(RichTextNodeKind.Table, Children: [
                    new RichTextNode(RichTextNodeKind.TableRow, Children: [Cell("H", true)]),
                    new RichTextNode(RichTextNodeKind.TableRow, Children: [Cell("D", false)])])),
            new CalloutBlock("c", CalloutTone.Warning, "Careful", [Para(Text("callout body"))]));

        var html = await RenderDocument(doc);

        foreach (var expected in new[]
        {
            "<h2 id=\"two\">Two</h2>", "<h3 id=\"three\">", "<h4 id=\"four\">", "<p>", "<em>emph</em>", "<strong>strong</strong>",
            "<code>inline</code>", "<a href=\"/about\">about</a>", "<ul>", "<ol>", "<li>one</li>", "<blockquote>",
            "<pre><code class=\"language-brainfuck\">x</code></pre>", "<figure>", "loading=\"lazy\"", "decoding=\"async\"",
            "src=\"/media/m1\"", "alt=\"An alt\"", "<figcaption>A caption</figcaption>", "<table>", "<tr>", "<th>H</th>", "<td>D</td>",
            "<aside class=\"callout callout--warning\"", "callout body", "Careful",
        })
        {
            html.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task HeadingsGetStableDeduplicatedIds()
    {
        var html = await RenderDocument(Doc(Rich("a", Heading(2, "Setup"), Heading(2, "Setup"), Heading(2, "Setup"))));

        html.ShouldContain("<h2 id=\"setup\">");
        html.ShouldContain("<h2 id=\"setup-2\">");
        html.ShouldContain("<h2 id=\"setup-3\">");
    }

    [Fact]
    public async Task HeadingIds_AreUniqueAcrossBlocks()
    {
        var html = await RenderDocument(Doc(Rich("a", Heading(2, "Setup")), Rich("b", Heading(2, "Setup!"))));

        html.ShouldContain("id=\"setup\"");
        html.ShouldContain("id=\"setup-2\"");
    }

    [Fact]
    public async Task MaliciousContent_IsEscaped()
    {
        var html = await RenderDocument(Doc(Rich("a", Para(Text("<script>x</script>")))));

        html.ShouldContain("&lt;script&gt;");
        html.ShouldNotContain("<script");
    }

    [Fact]
    public async Task InvalidDocument_RendersNothing()
    {
        var bad = new RichTextNode(RichTextNodeKind.Link, Url: "javascript:alert(1)", Children: [Text("Bad")]);

        var html = await RenderDocument(Doc(Rich("a", Para(bad))));

        html.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task RichTextView_UnsafeLink_RendersChildTextWithoutAnchor()
    {
        var bad = new RichTextNode(RichTextNodeKind.Link, Url: "javascript:alert(1)", Children: [Text("Bad")]);

        var html = await Render<RichTextView>(new() { ["Nodes"] = ImmutableArray.Create(Para(bad)) });

        html.ShouldContain("Bad");
        html.ShouldNotContain("<a");
        html.ShouldNotContain("javascript:");
    }

    [Fact]
    public async Task RichTextView_ToleratesOddShapes()
    {
        var odd = ImmutableArray.Create(
            new RichTextNode(RichTextNodeKind.Paragraph),
            new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: 9, Children: [Text("odd")]),
            new RichTextNode(RichTextNodeKind.Text),
            new RichTextNode(RichTextNodeKind.Image),
            new RichTextNode(RichTextNodeKind.CodeBlock));

        var html = await Render<RichTextView>(new() { ["Nodes"] = odd });

        html.ShouldContain("odd");
    }

    [Fact]
    public async Task CodeBlock_KnownLanguage_IsHighlighted()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "public class A { }", Language: "csharp");

        var html = await RenderDocument(Doc(Rich("a", code)));

        html.ShouldContain("<pre><code class=\"language-csharp\">");
        html.ShouldContain("<span class=");
        html.ShouldContain("public");
    }

    [Fact]
    public async Task CodeBlock_UnknownLanguage_RendersEscapedPlainPre()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "a<b", Language: "brainfuck");

        var html = await RenderDocument(Doc(Rich("a", code)));

        html.ShouldContain("<pre><code class=\"language-brainfuck\">a&lt;b</code></pre>");
    }

    [Fact]
    public async Task CodeBlock_KnownLanguage_EscapesMarkup()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "var s = \"<script>\";", Language: "cs");

        var html = await RenderDocument(Doc(Rich("a", code)));

        html.ShouldNotContain("<script");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public async Task Image_Unresolvable_RendersCaptionTextWithoutImg()
    {
        var image = new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("gone"), Alt: "Alt words", Caption: "Caption words");
        var gallery = new GalleryBlock("g", [new GalleryImage(new MediaRef("gone"), "Gallery alt", null)], null);

        var html = await RenderDocument(Doc(Rich("a", image), gallery), resolves: false);

        html.ShouldNotContain("<img");
        html.ShouldContain("Caption words");
        html.ShouldContain("Gallery alt");
    }

    [Fact]
    public async Task Gallery_RendersResolvedImages()
    {
        var gallery = new GalleryBlock("g", [new GalleryImage(new MediaRef("p1"), "First", "Cap one")], "My gallery");

        var html = await RenderDocument(Doc(gallery));

        html.ShouldContain("My gallery");
        html.ShouldContain("src=\"/media/p1\"");
        html.ShouldContain("srcset=");
        html.ShouldContain("alt=\"First\"");
        html.ShouldContain("loading=\"lazy\"");
        html.ShouldContain("Cap one");
    }

    [Fact]
    public async Task Cta_RendersLink()
    {
        var html = await RenderDocument(Doc(new CtaBlock("cta", "Join us", "Body text", "Sign up", "/join")));

        html.ShouldContain("Join us");
        html.ShouldContain("Body text");
        html.ShouldContain("href=\"/join\"");
        html.ShouldContain("Sign up");
    }

    [Fact]
    public async Task UnknownBlock_And_UnregisteredExtension_RenderNothing_OtherBlocksStillRender()
    {
        using var raw = JsonDocument.Parse("""{"$block":"future","id":"f1","secret":"hidden-value"}""");
        using var data = JsonDocument.Parse("""{"name":"Registered card"}""");
        var doc = Doc(
            new UnknownBlock("f1", raw.RootElement.Clone()),
            new ExtensionBlock("e1", "not-registered", data.RootElement.Clone()),
            new ExtensionBlock("e2", "project-card", data.RootElement.Clone()),
            Rich("a", Para(Text("Still here"))));

        var html = await RenderDocument(doc);

        html.ShouldNotContain("hidden-value");
        html.ShouldNotContain("future");
        html.ShouldContain("<div class=\"project-card\">Registered card</div>");
        html.ShouldContain("Still here");
    }

    [Fact]
    public async Task ExternalLink_GetsNoopener_InternalLinkDoesNot()
    {
        var doc = Doc(Rich("a", Para(
            new RichTextNode(RichTextNodeKind.Link, Url: "https://example.com/x", Children: [Text("ext")]),
            new RichTextNode(RichTextNodeKind.Link, Url: "/local", Children: [Text("int")]))));

        var html = await RenderDocument(doc);

        html.ShouldContain("<a href=\"https://example.com/x\" rel=\"noopener\">ext</a>");
        html.ShouldContain("<a href=\"/local\">int</a>");
    }

    [Fact]
    public void ColorCodeHighlighter_SupportsAliases_AndRejectsOthers()
    {
        var highlighter = new ColorCodeHighlighter();

        foreach (var language in new[] { "cs", "csharp", "js", "javascript", "ts", "typescript", "html", "xml", "css", "sql", "powershell", "ps1", "python", "py", "java", "cpp", "fsharp", "php" })
        {
            highlighter.Highlight("x", language).ShouldNotBeNull(language);
        }

        highlighter.Highlight("x", "brainfuck").ShouldBeNull();
        highlighter.Highlight("x", null).ShouldBeNull();
    }

    [Fact]
    public void Registry_AddAndTryGet()
    {
        var registry = new ContentBlockRegistry().Add("a", "A", typeof(ProjectCard));

        registry.TryGet("a", out var found).ShouldBeTrue();
        found.Label.ShouldBe("A");
        found.CreateDefault().ValueKind.ShouldBe(JsonValueKind.Object);
        registry.TryGet("b", out _).ShouldBeFalse();
    }

    [Fact]
    public void Registry_DuplicateType_Throws()
    {
        var registry = new ContentBlockRegistry().Add("a", "A", typeof(ProjectCard));

        Should.Throw<InvalidOperationException>(() => registry.Add("a", "A2", typeof(ProjectCard)));
    }

    [Fact]
    public async Task ThrowingExtensionRenderer_RendersNothing_AndRestOfDocumentRenders()
    {
        using var data = JsonDocument.Parse("""{"name":"Zed"}""");
        var doc = Doc(
            Rich("a", Para(Text("before"))),
            new ExtensionBlock("e1", "exploding", data.RootElement.Clone()),
            new ExtensionBlock("e2", "project-card", data.RootElement.Clone()),
            Rich("b", Para(Text("after"))));

        var html = await RenderDocument(doc);

        html.ShouldContain("before");
        html.ShouldContain("Zed");
        html.ShouldContain("after");
        html.ShouldNotContain("boom");
    }

    [Fact]
    public async Task CodeBlock_HighlightedMarkupOutsideStrings_IsEscaped()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: "a<b", Language: "cs");

        var html = await RenderDocument(Doc(Rich("a", code)));

        html.ShouldContain("&lt;");
        html.ShouldNotContain("a<b");
    }

    [Fact]
    public async Task CtaBlockView_UnsafeUrl_RendersNoAnchor()
    {
        var html = await Render<CtaBlockView>(new() { ["Block"] = new CtaBlock("c", "Head", null, "Go", "javascript:alert(1)") });

        html.ShouldContain("Head");
        html.ShouldNotContain("<a");
    }

    [Fact]
    public async Task RichTextView_ToleratesNullNode_NullLinkUrl_AndDeepNesting()
    {
        RichTextNode deep = Text("deep");
        for (var i = 0; i < 40; i++)
        {
            deep = new RichTextNode(RichTextNodeKind.Quote, Children: [deep]);
        }

        var nodes = ImmutableArray.Create(
            null!,
            Para(new RichTextNode(RichTextNodeKind.Link, Url: null, Children: [Text("nolink")])),
            deep);

        var html = await Render<RichTextView>(new() { ["Nodes"] = nodes });

        html.ShouldContain("nolink");
        html.ShouldNotContain("<a");
    }
}
