using System.Collections.Immutable;

namespace SyntaxCircus.RichContent.Tests;

public class ValidationCompletenessTests
{
    private static RichTextNode Text(string t) => new(RichTextNodeKind.Text, Text: t);
    private static RichTextNode Para(string t) => new(RichTextNodeKind.Paragraph, Children: [Text(t)]);
    private static RichTextBlock Rich(string id, string text = "x") => new(id, [Para(text)]);
    private static ContentDocument Doc(params ContentBlock[] blocks) => new(1, [.. blocks]);

    [Fact]
    public void Validate_DuplicateIds_AttributesErrorToTheLaterOccurrence()
    {
        var errors = RichContentRules.Validate(Doc(Rich("intro"), Rich("intro")));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("duplicate-block-id");
        error.BlockIndex.ShouldBe(1);
    }

    [Fact]
    public void Validate_MalformedKnownBlockSharingAValidId_IsAttributedByIndex()
    {
        var malformed = ContentDocumentSerializer.Deserialize("""{"schemaVersion":1,"blocks":[{"$block":"gallery","id":"x"}]}""").Document!.Blocks[0];

        var errors = RichContentRules.Validate(Doc(Rich("x"), malformed));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("malformed-known-block");
        error.BlockIndex.ShouldBe(1);
    }

    [Fact]
    public void Validate_AfterTextBudgetIsBlown_LaterBlocksAreStillValidated()
    {
        var bad = new RichTextBlock("b2", [new RichTextNode(RichTextNodeKind.Image, Media: new MediaRef("m"), Alt: "")]);
        var limits = RichContentLimits.Default with { TextBytes = 10 };

        var errors = RichContentRules.Validate(Doc(Rich("b1", new string('a', 50)), bad), limits);

        errors.Count(e => e.Code == "text-too-large").ShouldBe(1);
        errors.ShouldContain(e => e.Code == "alt-required" && e.BlockId == "b2" && e.BlockIndex == 1);
    }

    [Fact]
    public void Validate_BlockTitleTooLong_IsReportedForEachField()
    {
        var long200 = new string('t', RichContentLimits.BlockTitleCharacters + 1);
        var ok = new string('t', RichContentLimits.BlockTitleCharacters);
        var doc = Doc(
            new CalloutBlock("c", CalloutTone.Info, long200, [Para("x")]),
            new GalleryBlock("g", [new GalleryImage(new MediaRef("m"), "alt", null)], long200),
            new CtaBlock("h", long200, null, "Go", "/x"),
            new CtaBlock("l", "Head", null, long200, "/x"),
            new CtaBlock("fine", ok, null, ok, "/x"));

        var errors = RichContentRules.Validate(doc);

        errors.Where(e => e.Code == "block-title-too-long").Select(e => e.BlockId).ShouldBe(["c", "g", "h", "l"]);
    }

    [Fact]
    public void Validate_CtaBodyTooLong_IsReported()
    {
        var doc = Doc(new CtaBlock("c", "H", new string('b', RichContentLimits.CtaBodyCharacters + 1), "Go", "/x"));

        RichContentRules.Validate(doc).ShouldHaveSingleItem().Code.ShouldBe("cta-body-too-long");
        RichContentRules.Validate(Doc(new CtaBlock("c", "H", new string('b', RichContentLimits.CtaBodyCharacters), "Go", "/x"))).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_BlockLevelStrings_CountTowardTheTextBudget()
    {
        var doc = Doc(new CtaBlock("c", "Heading text", "Body text", "Go", "/x"));

        var errors = RichContentRules.Validate(doc, RichContentLimits.Default with { TextBytes = 10 });

        errors.ShouldHaveSingleItem().Code.ShouldBe("text-too-large");
        RichContentRules.Validate(doc, RichContentLimits.Default with { TextBytes = 1000 }).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullGalleryImage_ReportsNullImage()
    {
        var gallery = new GalleryBlock("g", [null!], null);

        RichContentRules.Validate(Doc(gallery)).ShouldContain(e => e.Code == "null-image" && e.BlockIndex == 0);
    }

    [Fact]
    public void Validate_NullNodes_ReportsNullNode()
    {
        var block = new RichTextBlock("b", [null!, new RichTextNode(RichTextNodeKind.Paragraph, Children: [null!])]);

        RichContentRules.Validate(Doc(block)).Count(e => e.Code == "null-node").ShouldBe(2);
    }

    [Fact]
    public void Deserialize_NullGalleryImage_IsDropped()
    {
        var result = ContentDocumentSerializer.Deserialize("""{"schemaVersion":1,"blocks":[{"$block":"gallery","id":"g","images":[null]}]}""");

        var gallery = result.Document!.Blocks[0].ShouldBeOfType<GalleryBlock>();
        gallery.Images.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_NullNodesAtAnyDepth_AreDropped()
    {
        var json = """
            {"schemaVersion":1,"blocks":[
              {"$block":"rich-text","id":"r","nodes":[null,{"kind":"paragraph","children":[null,{"kind":"text","text":"hi"}]}]},
              {"$block":"callout","id":"c","tone":"info","nodes":[{"kind":"paragraph","children":[null]}]}]}
            """;

        var result = ContentDocumentSerializer.Deserialize(json);

        var rich = result.Document!.Blocks[0].ShouldBeOfType<RichTextBlock>();
        rich.Nodes.Length.ShouldBe(1);
        rich.Nodes[0].Children.Length.ShouldBe(1);
        rich.Nodes[0].Children[0].Text.ShouldBe("hi");
        var callout = result.Document.Blocks[1].ShouldBeOfType<CalloutBlock>();
        callout.Nodes[0].Children.ShouldBeEmpty();
    }
}
