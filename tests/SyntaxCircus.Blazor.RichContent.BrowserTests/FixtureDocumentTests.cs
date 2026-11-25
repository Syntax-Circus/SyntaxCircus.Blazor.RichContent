using System.Collections.Immutable;
using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>Checks the sample documents themselves; later browser tests rely on what they contain.</summary>
public class FixtureDocumentTests
{
    public static TheoryData<string> Names => [.. Fixtures.Names];

    [Fact]
    public void Names_AreTheDocumentedFixtures() =>
        Fixtures.Names.ShouldBe(["empty", "everything", "unknown", "post"]);

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryFixture_IsValid_AndSurvivesASerializerRoundTrip(string name)
    {
        var document = Fixtures.Get(name).ShouldNotBeNull();
        var json = ContentDocumentSerializer.Serialize(document);

        RichContentRules.Validate(document).ShouldBeEmpty();
        var read = ContentDocumentSerializer.Deserialize(json).Document.ShouldNotBeNull();
        ContentDocumentSerializer.Serialize(read).ShouldBe(json);
    }

    [Fact]
    public void UnknownName_IsNull() => Fixtures.Get("nope").ShouldBeNull();

    [Fact]
    public void Everything_HasEveryNodeAndBlockKind()
    {
        var document = Fixtures.Get("everything")!;
        var nodes = AllNodes(document).ToList();

        nodes.Select(n => n.Kind).Distinct().ShouldBe(Enum.GetValues<RichTextNodeKind>(), ignoreOrder: true);
        document.Blocks.Select(b => b.GetType()).Distinct().ShouldBe(
            [typeof(RichTextBlock), typeof(GalleryBlock), typeof(CalloutBlock), typeof(CtaBlock), typeof(ExtensionBlock)],
            ignoreOrder: true);
        nodes.Where(n => n.Kind == RichTextNodeKind.Heading).Select(n => n.HeadingLevel).Distinct().ShouldBe([2, 3, 4], ignoreOrder: true);
        nodes.Where(n => n.Kind == RichTextNodeKind.TableCell).Select(n => n.Header).Distinct().ShouldContain(true);
    }

    [Fact]
    public void Everything_HasEveryListShape()
    {
        var nodes = AllNodes(Fixtures.Get("everything")!).ToList();
        var lists = nodes.Where(n => n.Kind == RichTextNodeKind.List).ToList();
        var items = nodes.Where(n => n.Kind == RichTextNodeKind.ListItem).ToList();

        lists.Select(l => l.Ordered).Distinct().ShouldBe([true, false, null], ignoreOrder: true);

        // Inline form and paragraph form, each with and without a nested list.
        foreach (var paragraphForm in new[] { false, true })
        {
            foreach (var nested in new[] { false, true })
            {
                items.ShouldContain(i => (i.Children[0].Kind == RichTextNodeKind.Paragraph) == paragraphForm
                    && (i.Children[i.Children.Length - 1].Kind == RichTextNodeKind.List) == nested);
            }
        }
    }

    [Fact]
    public void Everything_StoresANonBreakingSpace_InATextRun() =>
        AllNodes(Fixtures.Get("everything")!).ShouldContain(n => n.Kind == RichTextNodeKind.Text && n.Text!.Contains(' '));

    [Fact]
    public void Everything_HasNoLockedNodes()
    {
        // Locked content belongs in "unknown"; a locked node here would hide a surface from the fidelity tests.
        foreach (var block in Fixtures.Get("everything")!.Blocks.OfType<RichTextBlock>())
        {
            SurfacePlanner.Plan(block.Nodes).ShouldNotContain(s => s.Kind == SegmentKind.Locked);
        }
    }

    [Fact]
    public void Unknown_HasAnUnknownBlock_AndUnknownPropertiesAtSeveralDepths()
    {
        var document = Fixtures.Get("unknown")!;
        var topLevel = document.Blocks.OfType<RichTextBlock>().SelectMany(b => b.Nodes).ToList();

        document.Blocks.OfType<UnknownBlock>().ShouldHaveSingleItem();
        document.Blocks.OfType<RichTextBlock>().ShouldContain(b => b.Extra != null);
        document.Blocks.OfType<CalloutBlock>().ShouldContain(b => b.Extra != null);
        document.Blocks.OfType<GalleryBlock>().SelectMany(b => b.Images).ShouldContain(i => i.Extra != null);
        topLevel.ShouldContain(n => n.Extra != null);
        topLevel.ShouldContain(n => n.Extra == null && Walk(n.Children).Any(c => c.Extra != null));

        var segments = SurfacePlanner.Plan(document.Blocks.OfType<RichTextBlock>().First().Nodes);
        segments.ShouldContain(s => s.Kind == SegmentKind.Locked);
        segments.ShouldContain(s => s.Kind == SegmentKind.Surface);
    }

    [Fact]
    public void Post_HasAboutSixtyTopLevelNodes() =>
        Fixtures.Get("post")!.Blocks.OfType<RichTextBlock>().Sum(b => b.Nodes.Length).ShouldBeInRange(55, 65);

    [Theory]
    [MemberData(nameof(Names))]
    public void Replaced_ChangesOnlyTheFirstTopLevelNode_InNewObjects(string name)
    {
        var original = Fixtures.Get(name)!;

        var replaced = Fixtures.Replaced(original);

        var before = original.Blocks.OfType<RichTextBlock>().First();
        var after = replaced.Blocks.OfType<RichTextBlock>().First();
        after.Id.ShouldBe(before.Id);
        Walk([after.Nodes[0]]).ShouldContain(n => n.Text == Fixtures.ReplacementText);
        after.Nodes.Length.ShouldBe(Math.Max(1, before.Nodes.Length));
        Json(after.Nodes.Skip(1)).ShouldBe(Json(before.Nodes.Skip(1)));
        Json(replaced.Blocks.Where(b => b.Id != after.Id)).ShouldBe(Json(original.Blocks.Where(b => b.Id != before.Id)));
        replaced.Blocks.ShouldAllBe(b => original.Blocks.All(o => !ReferenceEquals(o, b)));
        RichContentRules.Validate(replaced).ShouldBeEmpty();
    }

    private static string Json(IEnumerable<RichTextNode> nodes) => Json([new RichTextBlock("x", [.. nodes])]);

    private static string Json(IEnumerable<ContentBlock> blocks) =>
        ContentDocumentSerializer.Serialize(new ContentDocument(1, [.. blocks]));

    private static IEnumerable<RichTextNode> AllNodes(ContentDocument document) =>
        document.Blocks.SelectMany(b => b switch
        {
            RichTextBlock rich => Walk(rich.Nodes),
            CalloutBlock callout => Walk(callout.Nodes),
            _ => [],
        });

    private static IEnumerable<RichTextNode> Walk(ImmutableArray<RichTextNode> nodes) =>
        nodes.IsDefaultOrEmpty ? [] : nodes.SelectMany(n => Walk(n.Children).Prepend(n));
}
