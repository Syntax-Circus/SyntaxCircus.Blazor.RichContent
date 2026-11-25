using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class SurfacePlannerTests
{
    private static RichTextNode Node(Kind kind, params RichTextNode[] children) => new(kind, Children: [.. children]);

    private static RichTextNode Text(string text = "x") => new(Kind.Text, Text: text);

    private static RichTextNode Paragraph() => Node(Kind.Paragraph, Text());

    private static RichTextNode Heading(int? level = 2) => new(Kind.Heading, HeadingLevel: level, Children: [Text()]);

    private static RichTextNode Link(string? url, params RichTextNode[] children) => new(Kind.Link, Url: url, Children: [.. children]);

    private static RichTextNode Bullets(params RichTextNode[] itemChildren) =>
        new(Kind.List, Ordered: false, Children: [Node(Kind.ListItem, itemChildren)]);

    private static RichTextNode Code() => new(Kind.CodeBlock, Text: "var x = 1;", Language: "csharp");

    private static RichTextNode Image() => new(Kind.Image, Media: new MediaRef("m1"), Alt: "alt", Caption: "caption");

    private static RichTextNode Cell(params RichTextNode[] children) => new(Kind.TableCell, Header: false, Children: [.. children]);

    private static RichTextNode Table(params RichTextNode[] cells) => Node(Kind.Table, Node(Kind.TableRow, cells));

    private static RichTextNode WithExtra(RichTextNode node) =>
        node with { Extra = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(1) } };

    private static SegmentKind Only(RichTextNode node) => SurfacePlanner.Plan([node]).ShouldHaveSingleItem().Kind;

    [Fact]
    public void ProseRunsAreOneSurface_AndSpecialNodesSplitThem()
    {
        ImmutableArray<RichTextNode> nodes =
            [Paragraph(), Heading(), Code(), Bullets(Text()), Image(), Node(Kind.Quote, Paragraph())];

        SurfacePlanner.Plan(nodes).ShouldBe(
        [
            new Segment(SegmentKind.Surface, 0, 2),
            new Segment(SegmentKind.Code, 2, 1),
            new Segment(SegmentKind.Surface, 3, 1),
            new Segment(SegmentKind.Image, 4, 1),
            new Segment(SegmentKind.Surface, 5, 1),
        ]);
    }

    [Fact]
    public void UnknownPropertyAnywhereInside_LocksThatTopLevelNodeOnly()
    {
        var marked = Node(Kind.Paragraph, Node(Kind.Strong, WithExtra(Text())));

        SurfacePlanner.Plan([Paragraph(), marked, Paragraph()]).ShouldBe(
        [
            new Segment(SegmentKind.Surface, 0, 1),
            new Segment(SegmentKind.Locked, 1, 1),
            new Segment(SegmentKind.Surface, 2, 1),
        ]);
    }

    [Fact]
    public void AdjacentCardsAndLockedNodes_AreOneSegmentEach()
    {
        SurfacePlanner.Plan([Code(), Code(), WithExtra(Paragraph()), WithExtra(Paragraph())]).ShouldBe(
        [
            new Segment(SegmentKind.Code, 0, 1),
            new Segment(SegmentKind.Code, 1, 1),
            new Segment(SegmentKind.Locked, 2, 1),
            new Segment(SegmentKind.Locked, 3, 1),
        ]);
    }

    [Fact]
    public void EmptyExtraDictionary_DoesNotLock() =>
        SurfaceSupport.CanEdit(Paragraph() with { Extra = new Dictionary<string, JsonElement>() }).ShouldBeTrue();

    [Theory]
    [InlineData("T", true)]
    [InlineData("TTL", true)]
    [InlineData("TLL", true)]
    [InlineData("P", true)]
    [InlineData("PPL", true)]
    [InlineData("L", true)]
    [InlineData("TP", false)]
    [InlineData("PT", false)]
    [InlineData("LT", false)]
    [InlineData("LP", false)]
    [InlineData("TLT", false)]
    [InlineData("PLP", false)]
    public void ListItemShapes(string shape, bool editable)
    {
        RichTextNode[] children =
        [
            .. shape.Select(c => c switch
            {
                'T' => Text(),
                'P' => Paragraph(),
                _ => Bullets(Text()),
            }),
        ];

        SurfaceSupport.CanEdit(Bullets(children)).ShouldBe(editable);
        // The same rule applies to a list item that is nested further down.
        SurfaceSupport.CanEdit(Node(Kind.Quote, Bullets(children))).ShouldBe(editable);
    }

    [Fact]
    public void EmptyNodes_PlanIsEmpty()
    {
        SurfacePlanner.Plan(default).ShouldBeEmpty();
        SurfacePlanner.Plan([]).ShouldBeEmpty();
    }

    [Fact]
    public void EverySupportedProseShape_IsEditable()
    {
        var inline = new[]
        {
            Text(),
            Node(Kind.Emphasis, Node(Kind.Strong, Text())),
            new RichTextNode(Kind.InlineCode, Text: "code"),
            Link("/x", Node(Kind.Strong, Text())),
        };

        RichTextNode[] nodes =
        [
            Node(Kind.Paragraph, inline),
            Heading(2),
            Heading(3),
            Heading(4),
            new(Kind.List, Ordered: true, Children: [Node(Kind.ListItem, Node(Kind.Paragraph, inline), Bullets(inline))]),
            Node(Kind.List, Node(Kind.ListItem, inline)),
            Node(Kind.Quote, Paragraph(), Bullets(Text())),
        ];

        nodes.ShouldAllBe(node => SurfaceSupport.CanEdit(node));
    }

    [Theory]
    [InlineData(Kind.Text)]
    [InlineData(Kind.Emphasis)]
    [InlineData(Kind.Strong)]
    [InlineData(Kind.InlineCode)]
    [InlineData(Kind.Link)]
    [InlineData(Kind.ListItem)]
    [InlineData(Kind.TableRow)]
    [InlineData(Kind.TableCell)]
    [InlineData((Kind)999)]
    public void KindThatCannotBeTopLevel_IsLocked(Kind kind)
    {
        var node = kind switch
        {
            Kind.Text or Kind.InlineCode => new RichTextNode(kind, Text: "x"),
            Kind.Link => Link("/x", Text()),
            _ => Node(kind, Text()),
        };

        SurfaceSupport.CanEdit(node).ShouldBeFalse();
        Only(node).ShouldBe(SegmentKind.Locked);
    }

    [Theory]
    [InlineData(Kind.CodeBlock)]
    [InlineData(Kind.Image)]
    [InlineData(Kind.Table)]
    public void CardKinds_AreNotSurfaceEditable(Kind kind)
    {
        var node = kind switch
        {
            Kind.CodeBlock => Code(),
            Kind.Image => Image(),
            _ => Table(Cell(Text())),
        };

        SurfaceSupport.CanEdit(node).ShouldBeFalse();
    }

    [Fact]
    public void ChildNotAllowedByItsParent_IsLocked()
    {
        RichTextNode[] nodes =
        [
            Node(Kind.Paragraph, Paragraph()),
            Node(Kind.Paragraph, Node(Kind.Strong, Bullets(Text()))),
            Node(Kind.Quote, Heading()),
            Node(Kind.Quote, Text()),
            Node(Kind.Quote, Node(Kind.Quote, Paragraph())),
            Node(Kind.List, Paragraph()),
            Bullets(Heading()),
            Bullets(Code()),
            Node(Kind.Paragraph, Image()),
            Node(Kind.Paragraph, new RichTextNode((Kind)999, Text: "x")),
        ];

        nodes.ShouldAllBe(node => !SurfaceSupport.CanEdit(node));
    }

    [Fact]
    public void LinkInsideALink_IsLockedAtAnyDistance()
    {
        SurfaceSupport.CanEdit(Node(Kind.Paragraph, Link("/a", Link("/b", Text())))).ShouldBeFalse();
        SurfaceSupport.CanEdit(Node(Kind.Paragraph, Link("/a", Node(Kind.Strong, Link("/b", Text()))))).ShouldBeFalse();
        SurfaceSupport.CanEdit(Node(Kind.Paragraph, Link("/a", Text()), Link("/b", Text()))).ShouldBeTrue();
    }

    [Fact]
    public void FieldTheKindMayNotHave_IsLocked()
    {
        var paragraph = Paragraph();
        RichTextNode[] nodes =
        [
            paragraph with { Text = "" },
            paragraph with { Url = "/x" },
            paragraph with { HeadingLevel = 2 },
            paragraph with { Ordered = false },
            paragraph with { Language = "csharp" },
            paragraph with { Media = new MediaRef("m1") },
            paragraph with { Alt = "" },
            paragraph with { Caption = "" },
            paragraph with { Header = false },
            Node(Kind.Paragraph, Text() with { Url = "/x" }),
            Node(Kind.Paragraph, Text() with { Children = [Text()] }),
            Node(Kind.Paragraph, new RichTextNode(Kind.InlineCode, Text: "x", Children: [Text()])),
        ];

        nodes.ShouldAllBe(node => !SurfaceSupport.CanEdit(node));
    }

    [Fact]
    public void MissingRequiredField_OrUnshowableHeadingLevel_IsLocked()
    {
        RichTextNode[] nodes =
        [
            Node(Kind.Paragraph, new RichTextNode(Kind.Text)),
            Node(Kind.Paragraph, new RichTextNode(Kind.InlineCode)),
            Node(Kind.Paragraph, Link(null, Text())),
            Heading(null),
            Heading(1),
            Heading(5),
        ];

        nodes.ShouldAllBe(node => !SurfaceSupport.CanEdit(node));
    }

    [Fact]
    public void ContentTheAuthorCanFix_StaysEditable()
    {
        var limits = RichContentLimits.Default;
        RichTextNode[] nodes =
        [
            Node(Kind.Paragraph, Link("javascript:alert(1)", Text())),
            Node(Kind.Paragraph, Link("", Text())),
            Node(Kind.Paragraph, Text(new string('a', limits.TextBytes + 1))),
            // Empty containers, as left behind when the author deletes everything in them.
            new(Kind.Paragraph),
            Node(Kind.Paragraph),
            Heading() with { Children = [] },
            Bullets(),
        ];

        nodes.ShouldAllBe(node => SurfaceSupport.CanEdit(node));
    }

    [Fact]
    public void NestingDeeperThanTheDefaultLimit_IsLocked()
    {
        static RichTextNode Nested(int depth)
        {
            var node = Text();
            for (var i = 2; i < depth; i++)
            {
                node = Node(Kind.Strong, node);
            }

            return Node(Kind.Paragraph, node);
        }

        SurfaceSupport.CanEdit(Nested(RichContentLimits.Default.Depth)).ShouldBeTrue();
        SurfaceSupport.CanEdit(Nested(RichContentLimits.Default.Depth + 1)).ShouldBeFalse();
        // Far past the limit must not overflow the stack.
        SurfaceSupport.CanEdit(Nested(100_000)).ShouldBeFalse();
    }

    [Fact]
    public void NullNodes_AreLocked_NotAnError()
    {
        SurfaceSupport.CanEdit(Node(Kind.Paragraph, (RichTextNode)null!)).ShouldBeFalse();
        Only(null!).ShouldBe(SegmentKind.Locked);
    }

    [Fact]
    public void CodeAndImage_AreCards_UnlessTheyCarryUnknownProperties()
    {
        Only(Code()).ShouldBe(SegmentKind.Code);
        Only(Image()).ShouldBe(SegmentKind.Image);
        Only(WithExtra(Code())).ShouldBe(SegmentKind.Locked);
        Only(WithExtra(Image())).ShouldBe(SegmentKind.Locked);
    }

    [Fact]
    public void UnfinishedCodeAndImage_StayCards()
    {
        Only(new RichTextNode(Kind.CodeBlock, Text: "", Language: "Not A Language")).ShouldBe(SegmentKind.Code);
        Only(new RichTextNode(Kind.Image, Alt: "")).ShouldBe(SegmentKind.Image);
        Only(new RichTextNode(Kind.Image)).ShouldBe(SegmentKind.Image);
    }

    [Fact]
    public void CodeAndImage_InAnUnsupportedShape_AreLocked()
    {
        Only(new RichTextNode(Kind.CodeBlock)).ShouldBe(SegmentKind.Locked);
        Only(Code() with { Children = [Text()] }).ShouldBe(SegmentKind.Locked);
        Only(Code() with { Url = "/x" }).ShouldBe(SegmentKind.Locked);
        Only(Image() with { Text = "x" }).ShouldBe(SegmentKind.Locked);
        Only(Image() with { Children = [Text()] }).ShouldBe(SegmentKind.Locked);
    }

    [Fact]
    public void TableWithInlineCells_IsATableCard()
    {
        var table = Node(
            Kind.Table,
            Node(Kind.TableRow, Cell(Link("/x", Text())) with { Header = true }, Cell(new RichTextNode(Kind.InlineCode, Text: "c"))),
            Node(Kind.TableRow, Cell(), new RichTextNode(Kind.TableCell)));

        Only(table).ShouldBe(SegmentKind.Table);
    }

    [Fact]
    public void TableWithBlockInCell_IsLocked()
    {
        Only(Table(Cell(Text()), Cell(Paragraph()))).ShouldBe(SegmentKind.Locked);
        Only(Table(Cell(Bullets(Text())))).ShouldBe(SegmentKind.Locked);
    }

    [Fact]
    public void TableInAnUnsupportedShape_IsLocked()
    {
        Only(WithExtra(Table(Cell(Text())))).ShouldBe(SegmentKind.Locked);
        Only(Node(Kind.Table, WithExtra(Node(Kind.TableRow, Cell(Text()))))).ShouldBe(SegmentKind.Locked);
        Only(Table(WithExtra(Cell(Text())))).ShouldBe(SegmentKind.Locked);
        Only(Table(Cell(Node(Kind.Strong, WithExtra(Text()))))).ShouldBe(SegmentKind.Locked);
        Only(Node(Kind.Table, Cell(Text()))).ShouldBe(SegmentKind.Locked);
        Only(Table(Text())).ShouldBe(SegmentKind.Locked);
        Only(Table(Cell(Text()) with { Url = "/x" })).ShouldBe(SegmentKind.Locked);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void CellContent_HasTheSameDepthLimit_AloneAndInsideItsTable(int over, bool editable)
    {
        // Table, row and cell take three of the levels.
        var content = Text();
        for (var i = 1; i < RichContentLimits.Default.Depth - 3 + over; i++)
        {
            content = Node(Kind.Strong, content);
        }

        SurfaceSupport.CanEditInline([content]).ShouldBe(editable);
        Only(Table(Cell(content))).ShouldBe(editable ? SegmentKind.Table : SegmentKind.Locked);
    }

    [Fact]
    public void CanEditInline_AcceptsOnlySupportedInlineContent()
    {
        SurfaceSupport.CanEditInline(default).ShouldBeTrue();
        SurfaceSupport.CanEditInline([]).ShouldBeTrue();
        SurfaceSupport.CanEditInline([Text(), Node(Kind.Emphasis, Text()), Link("bad url", Text())]).ShouldBeTrue();
        SurfaceSupport.CanEditInline([Text(), Paragraph()]).ShouldBeFalse();
        SurfaceSupport.CanEditInline([Bullets(Text())]).ShouldBeFalse();
        SurfaceSupport.CanEditInline([Node(Kind.Strong, WithExtra(Text()))]).ShouldBeFalse();
        SurfaceSupport.CanEditInline([Link("/a", Link("/b", Text()))]).ShouldBeFalse();
    }
}
