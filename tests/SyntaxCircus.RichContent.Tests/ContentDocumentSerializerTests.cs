using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.RichContent.Tests;

public class ContentDocumentSerializerTests
{
    private static ImmutableArray<RichTextNode> Nodes(params RichTextNode[] nodes) => [.. nodes];

    private static RichTextNode Text(string t) => new(RichTextNodeKind.Text, Text: t);

    private static ContentDocument BuildFullDocument()
    {
        var media = new MediaRef("m1", "large");
        var rich = new RichTextBlock("b1", Nodes(
            new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: 2, Children: Nodes(Text("Title"))),
            new RichTextNode(RichTextNodeKind.Paragraph, Children: Nodes(
                Text("plain "),
                new RichTextNode(RichTextNodeKind.Emphasis, Children: Nodes(Text("em"))),
                new RichTextNode(RichTextNodeKind.Strong, Children: Nodes(Text("strong"))),
                new RichTextNode(RichTextNodeKind.InlineCode, Text: "x = 1"),
                new RichTextNode(RichTextNodeKind.Link, Url: "https://example.com", Children: Nodes(Text("link"))))),
            new RichTextNode(RichTextNodeKind.List, Ordered: true, Children: Nodes(
                new RichTextNode(RichTextNodeKind.ListItem, Children: Nodes(Text("one"))),
                new RichTextNode(RichTextNodeKind.ListItem, Children: Nodes(Text("two"))))),
            new RichTextNode(RichTextNodeKind.Quote, Children: Nodes(Text("quoted"))),
            new RichTextNode(RichTextNodeKind.CodeBlock, Text: "var a = 1;", Language: "csharp"),
            new RichTextNode(RichTextNodeKind.Image, Media: media, Alt: "alt", Caption: "cap"),
            new RichTextNode(RichTextNodeKind.Table, Children: Nodes(
                new RichTextNode(RichTextNodeKind.TableRow, Children: Nodes(
                    new RichTextNode(RichTextNodeKind.TableCell, Header: true, Children: Nodes(Text("h"))),
                    new RichTextNode(RichTextNodeKind.TableCell, Header: false, Children: Nodes(Text("c")))))))));

        var gallery = new GalleryBlock("b2", [new GalleryImage(media, "alt", "cap"), new GalleryImage(new MediaRef("m2"), "alt2", null)], "Gallery");
        var callout = new CalloutBlock("b3", CalloutTone.Warning, "Careful", Nodes(new RichTextNode(RichTextNodeKind.Paragraph, Children: Nodes(Text("be careful")))));
        var cta = new CtaBlock("b4", "Join", "body", "Go", "https://example.com/go");
        using var data = JsonDocument.Parse("""{"slug":"p1","n":2}""");
        var extension = new ExtensionBlock("b5", "project-card", data.RootElement.Clone());

        return new ContentDocument(ContentDocument.CurrentSchemaVersion, [rich, gallery, callout, cta, extension]);
    }

    [Fact]
    public void RoundTrip_PreservesEveryBlockAndNodeKind()
    {
        var document = BuildFullDocument();

        var json = ContentDocumentSerializer.Serialize(document);
        var result = ContentDocumentSerializer.Deserialize(json);

        result.Error.ShouldBeNull();
        result.Document.ShouldNotBeNull();
        ContentDocumentSerializer.Serialize(result.Document).ShouldBe(json);
        result.Document.Blocks.Select(b => b.GetType()).ShouldBe(document.Blocks.Select(b => b.GetType()));

        var kinds = new HashSet<RichTextNodeKind>();
        void Walk(IEnumerable<RichTextNode> nodes)
        {
            foreach (var n in nodes)
            {
                kinds.Add(n.Kind);
                if (!n.Children.IsDefaultOrEmpty) Walk(n.Children);
            }
        }
        Walk(((RichTextBlock)result.Document.Blocks[0]).Nodes);
        kinds.ShouldBe(Enum.GetValues<RichTextNodeKind>(), ignoreOrder: true);
    }

    [Fact]
    public void Serialize_UsesStableWireNames()
    {
        var json = ContentDocumentSerializer.Serialize(BuildFullDocument());

        json.ShouldContain("\"$block\":\"rich-text\"");
        json.ShouldContain("\"kind\":\"codeBlock\"");
        json.ShouldContain("\"tone\":\"warning\"");
        json.ShouldContain("\"schemaVersion\":1");
    }

    [Fact]
    public void Deserialize_UnknownBlockType_BecomesUnknownBlock_AndRoundTripsVerbatim()
    {
        const string json = """{"schemaVersion":1,"blocks":[{"$block":"future-thing","id":"b9","x":1}]}""";

        var result = ContentDocumentSerializer.Deserialize(json);

        result.Error.ShouldBeNull();
        var unknown = result.Document!.Blocks.ShouldHaveSingleItem().ShouldBeOfType<UnknownBlock>();
        unknown.Id.ShouldBe("b9");
        var reserialized = ContentDocumentSerializer.Serialize(result.Document);
        reserialized.ShouldContain("\"x\":1");
        reserialized.ShouldContain("\"$block\":\"future-thing\"");
    }

    [Fact]
    public void Deserialize_UnsupportedSchemaVersion_ReturnsError()
    {
        var result = ContentDocumentSerializer.Deserialize("""{"schemaVersion":2,"blocks":[]}""");

        result.Document.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("schema", Case.Insensitive);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":1,"blocks":[{"$block":"cta","id":"b","heading":5}]}""")]
    public void Deserialize_MalformedJson_ReturnsError_DoesNotThrow(string json)
    {
        var result = ContentDocumentSerializer.Deserialize(json);

        result.Document.ShouldBeNull();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Empty_HasSingleEmptyRichTextBlock()
    {
        var document = ContentDocument.Empty("first");

        document.SchemaVersion.ShouldBe(ContentDocument.CurrentSchemaVersion);
        var block = document.Blocks.ShouldHaveSingleItem().ShouldBeOfType<RichTextBlock>();
        block.Id.ShouldBe("first");
        block.Nodes.ShouldBeEmpty();
    }
}
