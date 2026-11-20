using System.Collections.Immutable;
using System.Text.Json;

namespace SyntaxCircus.RichContent.Tests;

public class RichContentTextTests
{
    private static RichTextNode Text(string t) => new(RichTextNodeKind.Text, Text: t);
    private static RichTextNode Para(string t) => new(RichTextNodeKind.Paragraph, Children: [Text(t)]);
    private static RichTextNode Code(string t) => new(RichTextNodeKind.CodeBlock, Text: t);
    private static ContentDocument Doc(params ContentBlock[] blocks) => new(1, [.. blocks]);
    private static ContentDocument Prose(string text) => Doc(new RichTextBlock("b1", [Para(text)]));
    private static string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(i => "w" + i));

    [Fact]
    public void PlainText_JoinsBlocksAndSkipsCode()
    {
        using var data = JsonDocument.Parse("""{"a":"hidden"}""");
        using var raw = JsonDocument.Parse("""{"$block":"future","id":"u","text":"hidden"}""");
        var doc = Doc(
            new RichTextBlock("a", [
                new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: 2, Children: [Text("Title")]),
                new RichTextNode(RichTextNodeKind.Paragraph, Children: [Text("Hello "), new RichTextNode(RichTextNodeKind.Strong, Children: [Text("world")]), new RichTextNode(RichTextNodeKind.InlineCode, Text: "x")]),
                Code("var secret = 1;"),
                new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("m"), Alt: "alt hidden", Caption: "A caption"),
            ]),
            new CalloutBlock("c", CalloutTone.Tip, "Tip title", [Para("Callout text")]),
            new GalleryBlock("g", [new GalleryImage(new MediaRef("m"), "gallery alt", "Gallery caption")], null),
            new CtaBlock("cta", "Join us", "Body here", "Go", "/x"),
            new ExtensionBlock("e", "project-card", data.RootElement.Clone()),
            new UnknownBlock("u", raw.RootElement.Clone()));

        var text = RichContentText.PlainText(doc);

        text.ShouldContain("Title");
        text.ShouldContain("Hello worldx");
        text.ShouldContain("A caption");
        text.ShouldContain("Callout text");
        text.ShouldContain("Gallery caption");
        text.ShouldContain("Join us");
        text.ShouldContain("Body here");
        text.ShouldNotContain("secret");
        text.ShouldNotContain("hidden");
        text.ShouldNotContain("gallery alt");
        text.ShouldContain("\n\n");
        text.ShouldNotContain("Go");
    }

    [Fact]
    public void PlainText_NullChildren_DoesNotThrow()
    {
        var doc = Doc(new RichTextBlock("a", [new RichTextNode(RichTextNodeKind.Paragraph)]));

        RichContentText.PlainText(doc).ShouldBe("");
    }

    [Fact]
    public void WordCount_WeightsCodeAtOneThird()
    {
        var doc = Doc(new RichTextBlock("a", [Para(Words(300)), Code(Words(90))]));

        RichContentText.WordCount(doc).ShouldBe(330);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(225, 1)]
    [InlineData(226, 2)]
    public void ReadingMinutes_RoundsUpWithMinimumOne(int words, int expected)
    {
        var doc = words == 0 ? ContentDocument.Empty("a") : Prose(Words(words));

        RichContentText.ReadingMinutes(doc).ShouldBe(expected);
    }

    [Fact]
    public void Excerpt_CutsOnWordBoundary()
    {
        var excerpt = RichContentText.Excerpt(Prose("alpha beta gamma delta epsilon"), 14);

        excerpt.ShouldBe("alpha beta…");
        excerpt.ShouldEndWith("…");
    }

    [Fact]
    public void Excerpt_ShortText_NotTruncated() =>
        RichContentText.Excerpt(Prose("short text")).ShouldBe("short text");

    [Fact]
    public void Excerpt_SkipsLeadingHeadingAndCode_UsesFirstParagraph()
    {
        var doc = Doc(new RichTextBlock("a", [
            new RichTextNode(RichTextNodeKind.Heading, HeadingLevel: 2, Children: [Text("Heading")]),
            Code("code"),
            Para("The real intro.")]));

        RichContentText.Excerpt(doc).ShouldBe("The real intro.");
    }
}
