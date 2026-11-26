using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class SurfaceBufferTests
{
    private static RichTextNode Node(Kind kind, params RichTextNode[] children) => new(kind, Children: [.. children]);

    private static RichTextNode Text(string text) => new(Kind.Text, Text: text);

    private static RichTextNode Paragraph(string text) => Node(Kind.Paragraph, Text(text));

    private static SurfaceNode Wire(string kind, params SurfaceNode[] children) => new(kind, Children: children);

    private static SurfaceNode WireText(string text) => new("text", Text: text);

    private static SurfaceNode WireParagraph(string text) => Wire("paragraph", WireText(text));

    private static string Json(params RichTextNode[] nodes) => JsonSerializer.Serialize(nodes, ContentDocumentSerializer.Options);

    private static string Json(ImmutableArray<RichTextNode> nodes) => Json(nodes.ToArray());

    private static (SurfaceBuffer Buffer, string[] Keys, RichTextNode[] Originals) Loaded(params RichTextNode[] nodes)
    {
        var buffer = new SurfaceBuffer();
        var keys = buffer.Reset([.. nodes]).Select(n => n.Key).ToArray();
        return (buffer, keys, nodes);
    }

    private static SurfacePatch Patch(SurfaceBuffer buffer, string[] order, params KeyedNode[] changed) =>
        new(buffer.Generation, order, changed);

    [Fact]
    public void Reset_GivesEachNodeItsOwnKey_AndItsWireShape()
    {
        var buffer = new SurfaceBuffer();

        var keyed = buffer.Reset([Paragraph("a"), Paragraph("a")]);

        keyed.Count.ShouldBe(2);
        keyed.Select(k => k.Key).Distinct().Count().ShouldBe(2);
        keyed[1].Node.Children!.Single().Text.ShouldBe("a");
    }

    [Fact]
    public void Reset_IncrementsGeneration_AndNeverReusesKeys()
    {
        var buffer = new SurfaceBuffer();
        var before = buffer.Generation;

        var first = buffer.Reset([Paragraph("a")]);
        var firstGeneration = buffer.Generation;
        var second = buffer.Reset([Paragraph("a")]);

        firstGeneration.ShouldBe(before + 1);
        buffer.Generation.ShouldBe(before + 2);
        second[0].Key.ShouldNotBe(first[0].Key);
    }

    [Fact]
    public void Reset_DefaultArray_IsEmpty()
    {
        var buffer = new SurfaceBuffer();

        buffer.Reset(default).ShouldBeEmpty();
        buffer.Matches([]).ShouldBeTrue();
        buffer.Matches(default).ShouldBeTrue();
    }

    [Fact]
    public void Apply_KeepsTheSameObjectForUnchangedKeys()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"), Paragraph("b"), Paragraph("c"));

        var result = buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[1], WireParagraph("B"))));

        result.Length.ShouldBe(3);
        result[0].ShouldBeSameAs(originals[0]);
        result[1].ShouldNotBeSameAs(originals[1]);
        result[2].ShouldBeSameAs(originals[2]);
        Json(result).ShouldBe(Json(Paragraph("a"), Paragraph("B"), Paragraph("c")));
    }

    [Fact]
    public void Apply_ReordersInsertsAndRemoves()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"), Paragraph("b"), Paragraph("c"));

        var result = buffer.Apply(Patch(buffer, [keys[2], "c1", keys[0]], new KeyedNode("c1", WireParagraph("new"))));

        result.Length.ShouldBe(3);
        result[0].ShouldBeSameAs(originals[2]);
        result[2].ShouldBeSameAs(originals[0]);
        Json(result[1]).ShouldBe(Json(Paragraph("new")));

        // The inserted node is now known; the removed one is not.
        var next = buffer.Apply(Patch(buffer, ["c1"]));
        next.Single().ShouldBeSameAs(result[1]);
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c1", keys[1]])));
    }

    [Fact]
    public void Apply_EmptyOrder_EmptiesTheSurface()
    {
        var (buffer, _, _) = Loaded(Paragraph("a"));

        var result = buffer.Apply(Patch(buffer, []));

        result.IsDefault.ShouldBeFalse();
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Apply_UnknownOrDuplicateKey_Throws()
    {
        var (buffer, keys, _) = Loaded(Paragraph("a"), Paragraph("b"));
        var changed = new KeyedNode(keys[0], WireParagraph("x"));

        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, [keys[0], "nope"])));
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, [keys[0], keys[0]])));
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, keys, changed, changed)));
    }

    [Fact]
    public void Apply_ChangedKeyMissingFromOrder_Throws()
    {
        var (buffer, keys, _) = Loaded(Paragraph("a"), Paragraph("b"));

        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, [keys[0]], new KeyedNode(keys[1], WireParagraph("x")))));
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, keys, new KeyedNode("c1", WireParagraph("x")))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Apply_StaleOrFutureGeneration_Throws(int offset)
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"));

        Should.Throw<FormatException>(() => buffer.Apply(new(buffer.Generation + offset, keys, [])));

        buffer.Matches([.. originals]).ShouldBeTrue();
    }

    [Fact]
    public void Apply_PatchFromBeforeAReset_Throws()
    {
        var (buffer, keys, _) = Loaded(Paragraph("a"));
        var inFlight = Patch(buffer, keys, new KeyedNode(keys[0], WireParagraph("typed")));

        buffer.Reset([Paragraph("host")]);

        Should.Throw<FormatException>(() => buffer.Apply(inFlight));
        buffer.Matches([Paragraph("host")]).ShouldBeTrue();
    }

    [Fact]
    public void Apply_NodeLimit()
    {
        var buffer = new SurfaceBuffer();
        var limit = RichContentLimits.Default.Nodes;
        string[] Keys(int count) => [.. Enumerable.Range(0, count).Select(i => $"c{i}")];
        KeyedNode[] Added(int count) => [.. Keys(count).Select(k => new KeyedNode(k, Wire("paragraph")))];

        buffer.Apply(Patch(buffer, Keys(limit), Added(limit))).Length.ShouldBe(limit);
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, Keys(limit + 1), Added(limit + 1))));
    }

    [Theory]
    [InlineData("""{"generation":1,"changed":[]}""")]
    [InlineData("""{"generation":1,"order":[]}""")]
    [InlineData("""{"generation":1,"order":[null],"changed":[]}""")]
    [InlineData("""{"generation":1,"order":[""],"changed":[{"key":"","node":{"kind":"paragraph"}}]}""")]
    [InlineData("""{"generation":1,"order":["c1"],"changed":[null]}""")]
    [InlineData("""{"generation":1,"order":["c1"],"changed":[{"node":{"kind":"paragraph"}}]}""")]
    [InlineData("""{"generation":1,"order":["c1"],"changed":[{"key":"c1"}]}""")]
    [InlineData("""{"generation":1,"order":["c1"],"changed":[{"key":"c1","node":{}}]}""")]
    [InlineData("""{"generation":1,"order":["c1"],"changed":[{"key":"c1","node":{"kind":"paragraph","children":[null]}}]}""")]
    public void Apply_IncompletePatchFromTheWire_Throws(string json)
    {
        var buffer = new SurfaceBuffer();
        buffer.Reset([]);
        var patch = JsonSerializer.Deserialize<SurfacePatch>(json, JsonSerializerOptions.Web)!;

        Should.Throw<FormatException>(() => buffer.Apply(patch));
    }

    [Fact]
    public void Apply_RejectedPatch_ChangesNothing()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"), Paragraph("b"));

        Should.Throw<FormatException>(() => buffer.Apply(Patch(
            buffer,
            [keys[0], "c1"],
            new KeyedNode(keys[0], WireParagraph("changed")),
            new KeyedNode("c1", Wire("marquee")))));

        buffer.Matches([.. originals]).ShouldBeTrue();
        var result = buffer.Apply(Patch(buffer, keys));
        result[0].ShouldBeSameAs(originals[0]);
        result[1].ShouldBeSameAs(originals[1]);
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c1"])));
    }

    [Fact]
    public void Apply_RepeatedOrCumulativePatch_IsHarmless()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"), Paragraph("b"));
        var first = Patch(buffer, [keys[0], "c1"], new KeyedNode("c1", WireParagraph("new")));
        var applied = buffer.Apply(first);

        var repeated = buffer.Apply(first);
        var cumulative = buffer.Apply(Patch(
            buffer,
            [keys[0], "c1", "c2"],
            new KeyedNode("c1", WireParagraph("new")),
            new KeyedNode("c2", WireParagraph("more"))));

        repeated.Length.ShouldBe(2);
        repeated[0].ShouldBeSameAs(originals[0]);
        repeated[1].ShouldBeSameAs(applied[1]);
        cumulative[1].ShouldBeSameAs(applied[1]);
        Json(cumulative).ShouldBe(Json(Paragraph("a"), Paragraph("new"), Paragraph("more")));
    }

    [Fact]
    public void Apply_ChangedNodeWithTheSameValue_KeepsTheOriginalObject()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"));

        var result = buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[0], WireParagraph("a"))));

        result[0].ShouldBeSameAs(originals[0]);
    }

    [Fact]
    public void Apply_UnchangedNodeWithUnknownProperties_KeepsThem()
    {
        var extra = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(1) };
        var (buffer, keys, originals) = Loaded(Paragraph("a") with { Extra = extra }, Paragraph("b"));

        var result = buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[1], WireParagraph("B"))));

        result[0].ShouldBeSameAs(originals[0]);
        Json(result[0]).ShouldContain("\"x\":1");
    }

    [Theory]
    [InlineData("link")]
    [InlineData("list")]
    [InlineData("quote")]
    public void Apply_DropsAnEmptiedTopLevelContainer_AndStillKnowsItsKey(string kind)
    {
        // A top-level link only occurs in an inline surface.
        var inline = kind == "link";
        var buffer = new SurfaceBuffer(inline);
        var kept = inline ? Text("a") : Paragraph("a");
        var keys = buffer.Reset([kept]).Select(k => k.Key).ToArray();
        var emptied = new SurfaceNode(kind, Url: inline ? "https://example.com" : null);

        foreach (var node in new[] { emptied, emptied with { Children = [] } })
        {
            var result = buffer.Apply(Patch(buffer, [keys[0], "c1"], new KeyedNode("c1", node)));

            result.Single().ShouldBeSameAs(kept);
            buffer.Matches([kept]).ShouldBeTrue();
        }

        // The sender sends deltas, so it will not repeat a key it has already sent.
        buffer.Apply(Patch(buffer, [keys[0], "c1"])).Single().ShouldBeSameAs(kept);
    }

    [Fact]
    public void Apply_DroppedKey_ReturnsWhenTheAuthorFillsItAgain()
    {
        var (buffer, keys, _) = Loaded(Node(Kind.Quote, Paragraph("a")));
        buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[0], Wire("quote")))).ShouldBeEmpty();

        var result = buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[0], Wire("quote", WireParagraph("b")))));

        Json(result).ShouldBe(Json(Node(Kind.Quote, Paragraph("b"))));
    }

    [Fact]
    public void Apply_DropsEmptiedContainersAtAnyDepth_FromTheInsideOut()
    {
        var buffer = new SurfaceBuffer();
        var emptyLink = new SurfaceNode("link", Url: "https://example.com", Children: []);
        var emptyList = new SurfaceNode("list", Ordered: false, Children: []);

        var result = buffer.Apply(Patch(
            buffer,
            ["c1", "c2", "c3"],
            new KeyedNode("c1", Wire("paragraph", WireText("a"), emptyLink)),
            new KeyedNode("c2", Wire("quote", emptyList)),
            new KeyedNode("c3", new SurfaceNode("list", Ordered: true, Children: [Wire("listItem", emptyLink, emptyList)]))));

        Json(result).ShouldBe(Json(
            Paragraph("a"),
            new RichTextNode(Kind.List, Ordered: true, Children: [Node(Kind.ListItem)])));
        result.ShouldAllBe(n => SurfaceSupport.CanEdit(n));
    }

    public static TheoryData<string> UneditableNodes => new()
    {
        """{"kind":"text","text":"loose"}""",
        """{"kind":"paragraph","text":"x"}""",
        """{"kind":"paragraph","url":"https://example.com"}""",
        """{"kind":"paragraph","children":[{"kind":"text"}]}""",
        """{"kind":"paragraph","children":[{"kind":"paragraph"}]}""",
        """{"kind":"paragraph","children":[{"kind":"link","children":[{"kind":"text","text":"x"}]}]}""",
        """{"kind":"heading","level":1}""",
        """{"kind":"heading"}""",
        """{"kind":"quote","children":[{"kind":"quote","children":[{"kind":"paragraph"}]}]}""",
        """{"kind":"list","children":[{"kind":"listItem","children":[{"kind":"text","text":"x"},{"kind":"paragraph"}]}]}""",
    };

    [Theory]
    [MemberData(nameof(UneditableNodes))]
    public void Apply_NodeASurfaceCouldNotEditAgain_Throws(string node)
    {
        var buffer = new SurfaceBuffer();
        var wire = JsonSerializer.Deserialize<SurfaceNode>(node, JsonSerializerOptions.Web)!;

        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c1"], new KeyedNode("c1", wire))));
    }

    [Fact]
    public void Apply_NestingBeyondTheDepthASurfaceSupports_Throws()
    {
        var buffer = new SurfaceBuffer();

        // paragraph > (levels - 2) strong > text
        SurfaceNode Nested(int levels)
        {
            var node = WireText("x");
            for (var i = 0; i < levels - 2; i++)
            {
                node = Wire("strong", node);
            }

            return Wire("paragraph", node);
        }

        var deepest = RichContentLimits.Default.Depth;
        buffer.Apply(Patch(buffer, ["c1"], new KeyedNode("c1", Nested(deepest)))).Length.ShouldBe(1);
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c1"], new KeyedNode("c1", Nested(deepest + 1)))));
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c1"], new KeyedNode("c1", Nested(100_000)))));
    }

    [Fact]
    public void InlineBuffer_TakesInlineContentOnly()
    {
        var buffer = new SurfaceBuffer(inline: true);

        var result = buffer.Apply(Patch(
            buffer,
            ["c1", "c2"],
            new KeyedNode("c1", WireText("a")),
            new KeyedNode("c2", Wire("strong", WireText("b")))));

        SurfaceSupport.CanEditInline(result).ShouldBeTrue();
        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, ["c3"], new KeyedNode("c3", WireParagraph("x")))));
    }

    [Fact]
    public void Apply_TextOverALimitOrUnsafeUrl_IsAccepted_SoRulesCanReportIt()
    {
        var buffer = new SurfaceBuffer();
        var long_ = new string('x', RichContentLimits.Default.TextBytes + 1);
        var link = new SurfaceNode("link", Url: "javascript:alert(1)", Children: [WireText(long_)]);

        var result = buffer.Apply(Patch(buffer, ["c1"], new KeyedNode("c1", Wire("paragraph", link))));

        result.Single().Children.Single().Url.ShouldBe("javascript:alert(1)");
    }

    [Fact]
    public void Matches_EqualValuesInNewObjects_IsTrue()
    {
        var (buffer, _, _) = Loaded(Paragraph("a"), Node(Kind.Quote, Paragraph("b")));

        buffer.Matches([Paragraph("a"), Node(Kind.Quote, Paragraph("b"))]).ShouldBeTrue();
    }

    [Fact]
    public void Matches_AnyDifference_IsFalse()
    {
        var list = new RichTextNode(Kind.List, Children: [Node(Kind.ListItem)]);
        var (buffer, _, _) = Loaded(Paragraph("a"), list);
        var extra = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(1) };

        buffer.Matches([Paragraph("a")]).ShouldBeFalse();
        buffer.Matches([list, Paragraph("a")]).ShouldBeFalse();
        buffer.Matches([Paragraph("A"), list]).ShouldBeFalse();
        buffer.Matches([Paragraph("a") with { Extra = extra }, list]).ShouldBeFalse();
        buffer.Matches([Paragraph("a"), list with { Ordered = false }]).ShouldBeFalse();
        buffer.Matches([Paragraph("a"), list with { Children = [new RichTextNode(Kind.ListItem)] }]).ShouldBeFalse();
    }

    [Fact]
    public void Matches_FollowsAppliedPatches()
    {
        var (buffer, keys, _) = Loaded(Paragraph("a"));

        buffer.Apply(Patch(buffer, keys, new KeyedNode(keys[0], WireParagraph("b"))));

        buffer.Matches([Paragraph("b")]).ShouldBeTrue();
        buffer.Matches([Paragraph("a")]).ShouldBeFalse();
    }

    private static RichTextNode[] EverySupportedShape()
    {
        var link = new RichTextNode(Kind.Link, Url: "https://example.com/a?b=1&c=\"d\"", Children: [Text("link")]);
        var nested = new RichTextNode(Kind.List, Ordered: true, Children: [Node(Kind.ListItem, Text("deep"))]);
        return
        [
            Node(Kind.Paragraph, Text(" a b "), Node(Kind.Emphasis, Text("e")), Node(Kind.Strong, Node(Kind.Emphasis, link)),
                new RichTextNode(Kind.InlineCode, Text: "x < y")),
            new RichTextNode(Kind.Heading, HeadingLevel: 2, Children: [Text("two")]),
            new RichTextNode(Kind.Heading, HeadingLevel: 4, Children: [Text("")]),
            new RichTextNode(Kind.List, Ordered: false, Children: [Node(Kind.ListItem, Text("direct"), nested)]),
            new RichTextNode(Kind.List, Children: [Node(Kind.ListItem, Paragraph("wrapped"), Paragraph("twice"), nested)]),
            new RichTextNode(Kind.List, Ordered: true, Children: [Node(Kind.ListItem, nested), new RichTextNode(Kind.ListItem), Node(Kind.ListItem)]),
            Node(Kind.Quote, Paragraph("q"), nested),
            new RichTextNode(Kind.Paragraph),
            Node(Kind.Paragraph),
            Node(Kind.Paragraph, new RichTextNode(Kind.Strong), Node(Kind.Emphasis)),
        ];
    }

    [Theory]
    [InlineData("""{"kind":"list","text":"kept?"}""")]
    [InlineData("""{"kind":"list","url":"https://example.com"}""")]
    [InlineData("""{"kind":"list","level":2,"children":[]}""")]
    [InlineData("""{"kind":"quote","text":""}""")]
    [InlineData("""{"kind":"quote","ordered":true}""")]
    [InlineData("""{"kind":"paragraph","children":[{"kind":"link","url":"https://example.com","text":"kept?"}]}""")]
    [InlineData("""{"kind":"paragraph","children":[{"kind":"link","children":[]}]}""")]
    [InlineData("""{"kind":"quote","children":[{"kind":"list","text":"kept?"}]}""")]
    public void Apply_MalformedChildlessContainer_IsRejected_NotDropped(string node)
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"));
        var wire = JsonSerializer.Deserialize<SurfaceNode>(node, JsonSerializerOptions.Web)!;

        Should.Throw<FormatException>(() => buffer.Apply(Patch(buffer, [keys[0], "c1"], new KeyedNode("c1", wire))));

        buffer.Matches([.. originals]).ShouldBeTrue();
    }

    [Fact]
    public void ExceptionMessages_DoNotEchoWhatTheBrowserSent()
    {
        var huge = new string('k', 100_000);
        var (buffer, keys, _) = Loaded(Paragraph("a"));
        var changed = new KeyedNode(huge, WireParagraph("x"));

        SurfacePatch[] patches =
        [
            Patch(buffer, [huge]),
            Patch(buffer, [huge, huge], changed),
            Patch(buffer, [huge], changed, changed),
            Patch(buffer, keys, changed),
            Patch(buffer, ["c1"], new KeyedNode("c1", new SurfaceNode(huge))),
        ];

        foreach (var patch in patches)
        {
            Should.Throw<FormatException>(() => buffer.Apply(patch)).Message.ShouldNotContain("kkkk");
        }
    }

    [Fact]
    public void TryRebase_EqualValues_TakesTheNewObjects()
    {
        var (buffer, keys, _) = Loaded(Paragraph("a"), Paragraph("b"));
        RichTextNode[] copies = [Paragraph("a"), Paragraph("b")];

        buffer.TryRebase([.. copies]).ShouldBeTrue();

        var result = buffer.Apply(Patch(buffer, keys));
        result[0].ShouldBeSameAs(copies[0]);
        result[1].ShouldBeSameAs(copies[1]);
    }

    [Fact]
    public void TryRebase_DifferentValues_ChangesNothing()
    {
        var (buffer, keys, originals) = Loaded(Paragraph("a"));

        buffer.TryRebase([Paragraph("z")]).ShouldBeFalse();

        buffer.Apply(Patch(buffer, keys)).Single().ShouldBeSameAs(originals[0]);
    }

    [Fact]
    public void TryRebase_SkipsDroppedKeys_WhichStayKnown()
    {
        var (buffer, keys, _) = Loaded(Node(Kind.Quote, Paragraph("a")), Paragraph("b"));
        string[] order = ["c1", keys[0], keys[1]];
        buffer.Apply(Patch(buffer, order, new KeyedNode("c1", WireParagraph("new")), new KeyedNode(keys[0], Wire("quote"))));
        RichTextNode[] copies = [Paragraph("new"), Paragraph("b")];

        buffer.TryRebase([.. copies]).ShouldBeTrue();

        var result = buffer.Apply(Patch(buffer, order));
        result.Length.ShouldBe(2);
        result[0].ShouldBeSameAs(copies[0]);
        result[1].ShouldBeSameAs(copies[1]);
    }

    [Fact]
    public void Mapping_RoundTripsEverySupportedKind()
    {
        var nodes = EverySupportedShape();
        nodes.ShouldAllBe(n => SurfaceSupport.CanEdit(n));

        // Through the JSON the browser receives and sends back.
        var back = nodes
            .Select(n => JsonSerializer.Serialize(SurfaceNode.From(n), JsonSerializerOptions.Web))
            .Select(json => JsonSerializer.Deserialize<SurfaceNode>(json, JsonSerializerOptions.Web)!.ToNode())
            .ToArray();

        Json(back).ShouldBe(Json(nodes));
    }

    [Fact]
    public void Mapping_KeepsAbsentChildrenDistinctFromEmpty()
    {
        SurfaceNode.From(new RichTextNode(Kind.Paragraph)).Children.ShouldBeNull();
        SurfaceNode.From(Node(Kind.Paragraph)).Children.ShouldNotBeNull().ShouldBeEmpty();

        new SurfaceNode("paragraph").ToNode().Children.IsDefault.ShouldBeTrue();
        Wire("paragraph").ToNode().Children.IsDefault.ShouldBeFalse();

        Json(new SurfaceNode("paragraph").ToNode()).ShouldNotContain("children");
        Json(Wire("paragraph").ToNode()).ShouldContain("\"children\":[]");
    }

    [Fact]
    public void Mapping_KeepsAbsentOrderingDistinctFromFalse()
    {
        SurfaceNode.From(new RichTextNode(Kind.List)).Ordered.ShouldBeNull();
        SurfaceNode.From(new RichTextNode(Kind.List, Ordered: false)).Ordered.ShouldBe(false);

        Json(new SurfaceNode("list").ToNode()).ShouldNotContain("ordered");
        Json(new SurfaceNode("list", Ordered: false).ToNode()).ShouldContain("\"ordered\":false");
    }

    [Fact]
    public void Mapping_UsesTheSerializersKindNames()
    {
        foreach (var node in EverySupportedShape())
        {
            Json(node).ShouldContain($"\"kind\":\"{SurfaceNode.From(node).Kind}\"");
        }

        SurfaceNode.From(new RichTextNode(Kind.InlineCode, Text: "x")).Kind.ShouldBe("inlineCode");
        SurfaceNode.From(new RichTextNode(Kind.ListItem)).Kind.ShouldBe("listItem");
    }

    [Theory]
    [InlineData("marquee")]
    [InlineData("Paragraph")]
    [InlineData("0")]
    [InlineData("codeBlock")]
    [InlineData("image")]
    [InlineData("table")]
    [InlineData("tableRow")]
    [InlineData("tableCell")]
    [InlineData(null)]
    public void ToNode_UnknownKind_Throws(string? kind)
    {
        Should.Throw<FormatException>(() => new SurfaceNode(kind!).ToNode());
        Should.Throw<FormatException>(() => Wire("paragraph", new SurfaceNode(kind!)).ToNode());
    }

    [Theory]
    [InlineData(Kind.CodeBlock)]
    [InlineData(Kind.Image)]
    [InlineData(Kind.Table)]
    [InlineData(Kind.TableRow)]
    [InlineData(Kind.TableCell)]
    public void From_CardKind_Throws(Kind kind) =>
        Should.Throw<ArgumentException>(() => SurfaceNode.From(new RichTextNode(kind)));
}
