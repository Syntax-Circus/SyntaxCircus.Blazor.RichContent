using System.Collections.Immutable;
using System.Text.Json;

namespace SyntaxCircus.RichContent.Tests;

public class RichContentRulesTests
{
    private static ImmutableArray<RichTextNode> Nodes(params RichTextNode[] nodes) => [.. nodes];
    private static RichTextNode Text(string t) => new(RichTextNodeKind.Text, Text: t);
    private static RichTextNode Para(params RichTextNode[] children) => new(RichTextNodeKind.Paragraph, Children: [.. children]);
    private static ContentDocument Doc(params ContentBlock[] blocks) => new(1, [.. blocks]);
    private static ContentDocument DocOf(params RichTextNode[] nodes) => Doc(new RichTextBlock("b1", [.. nodes]));

    [Fact]
    public void Validate_FullTechnicalPost_IsValid()
    {
        var media = new MediaRef("m1");
        var filler = Para(Text(new string('x', 20 * 1024)));
        var rich = new RichTextBlock("intro", Nodes(
            new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: 2, Children: Nodes(Text("Intro"))),
            Para(Text("see "), new RichTextNode(RichTextNodeKind.Link, Url: "https://syntaxcircus.com/x", Children: Nodes(Text("this"))),
                new RichTextNode(RichTextNodeKind.InlineCode, Text: "x")),
            new RichTextNode(RichTextNodeKind.CodeBlock, Text: new string('c', 30 * 1024), Language: "csharp"),
            new RichTextNode(RichTextNodeKind.Image, Media: media, Alt: "diagram", Caption: "cap"),
            new RichTextNode(RichTextNodeKind.Table, Children: Nodes(
                new RichTextNode(RichTextNodeKind.TableRow, Children: Nodes(
                    new RichTextNode(RichTextNodeKind.TableCell, Header: true, Children: Nodes(Text("h"))),
                    new RichTextNode(RichTextNodeKind.TableCell, Children: Nodes(Text("c"))))))),
            new RichTextNode(RichTextNodeKind.Quote, Children: Nodes(Para(Text("quoted")))),
            new RichTextNode(RichTextNodeKind.List, Ordered: true, Children: Nodes(
                new RichTextNode(RichTextNodeKind.ListItem, Children: Nodes(Text("one"))))),
            filler, filler, filler, filler, filler, filler));
        var callout = new CalloutBlock("tip", CalloutTone.Tip, "Tip", Nodes(Para(Text("hi"))));
        var gallery = new GalleryBlock("gal", [new GalleryImage(media, "alt", null)], null);
        var cta = new CtaBlock("cta", "Join", null, "Go", "/contact");
        using var data = JsonDocument.Parse("""{"slug":"p"}""");
        var extension = new ExtensionBlock("project", "project-card", data.RootElement.Clone());

        RichContentRules.Validate(Doc(rich, callout, gallery, cta, extension)).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_CodeBlockOverLimit_ReportsCodeAndBlockId()
    {
        var code = new RichTextNode(RichTextNodeKind.CodeBlock, Text: new string('c', 32 * 1024 + 1));

        var errors = RichContentRules.Validate(Doc(new RichTextBlock("code1", Nodes(code))));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("code-block-too-large");
        error.BlockId.ShouldBe("code1");
    }

    [Fact]
    public void Validate_LinkWithUnsafeUrl_Fails() =>
        RichContentRules.Validate(DocOf(Para(new RichTextNode(RichTextNodeKind.Link, Url: "javascript:alert(1)", Children: Nodes(Text("x"))))))
            .ShouldContain(e => e.Code == "unsafe-url");

    [Fact]
    public void Validate_LinkNestedInLink_Fails()
    {
        var inner = new RichTextNode(RichTextNodeKind.Link, Url: "/b", Children: Nodes(Text("x")));
        var outer = new RichTextNode(RichTextNodeKind.Link, Url: "/a", Children: Nodes(new RichTextNode(RichTextNodeKind.Strong, Children: Nodes(inner))));

        RichContentRules.Validate(DocOf(Para(outer))).ShouldContain(e => e.Code == "nested-link");
    }

    [Fact]
    public void Validate_ImageWithoutAlt_Fails() =>
        RichContentRules.Validate(DocOf(new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("m"))))
            .ShouldContain(e => e.Code == "alt-required");

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Validate_HeadingLevelOutsideRange_Fails(int level) =>
        RichContentRules.Validate(DocOf(new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: level, Children: Nodes(Text("x")))))
            .ShouldContain(e => e.Code == "invalid-heading-level");

    [Fact]
    public void Validate_TableCellContainingParagraph_Fails()
    {
        var table = new RichTextNode(RichTextNodeKind.Table, Children: Nodes(
            new RichTextNode(RichTextNodeKind.TableRow, Children: Nodes(
                new RichTextNode(RichTextNodeKind.TableCell, Children: Nodes(Para(Text("x"))))))));

        RichContentRules.Validate(DocOf(table)).ShouldContain(e => e.Code == "invalid-node-parent");
    }

    [Fact]
    public void Validate_DuplicateBlockIds_Fails() =>
        RichContentRules.Validate(Doc(new RichTextBlock("a", Nodes(Para(Text("x")))), new RichTextBlock("a", Nodes(Para(Text("y"))))))
            .ShouldContain(e => e.Code == "duplicate-block-id" && e.BlockId == "a");

    [Fact]
    public void Validate_DepthOverLimit_Fails()
    {
        var node = Text("x");
        for (var i = 0; i < 9; i++)
        {
            node = new RichTextNode(RichTextNodeKind.Emphasis, Children: Nodes(node));
        }

        RichContentRules.Validate(DocOf(Para(node))).ShouldContain(e => e.Code == "depth-exceeded");
    }

    [Fact]
    public void Validate_UnknownBlock_IsNotAnError()
    {
        using var raw = JsonDocument.Parse("""{"$block":"future","id":"f1"}""");

        RichContentRules.Validate(Doc(new UnknownBlock("f1", raw.RootElement.Clone()))).ShouldBeEmpty();
    }
}
