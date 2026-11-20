using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed class EditingOperationsTests
{
    private static readonly RichTextNodeKind[] AllKinds = Enum.GetValues<RichTextNodeKind>();

    private static RichTextNode Valid(RichTextNodeKind kind) => RichTextEditorOperations.Create(kind) switch
    {
        { Kind: RichTextNodeKind.Image } image => image with { Media = new MediaRef("m1"), Alt = "alt" },
        { Kind: RichTextNodeKind.Link } link => link with { Url = "/x" },
        var other => other,
    };

    // Wraps a node in the ancestors it needs to be reachable from a top-level position.
    private static RichTextNode WithAncestors(RichTextNodeKind kind, RichTextNode node) => kind switch
    {
        RichTextNodeKind.ListItem => new(RichTextNodeKind.List, Children: [node]),
        RichTextNodeKind.TableRow => new(RichTextNodeKind.Table, Children: [node]),
        RichTextNodeKind.TableCell => new(RichTextNodeKind.Table, Children: [new(RichTextNodeKind.TableRow, Children: [node])]),
        RichTextNodeKind.Text or RichTextNodeKind.Emphasis or RichTextNodeKind.Strong
            or RichTextNodeKind.InlineCode or RichTextNodeKind.Link => new(RichTextNodeKind.Paragraph, Children: [node]),
        _ => node,
    };

    [Fact]
    public void Create_EveryKind_YieldsStructurallyValidNode()
    {
        foreach (var kind in AllKinds)
        {
            var node = RichTextEditorOperations.Create(kind);
            if (kind == RichTextNodeKind.Image)
            {
                node.Media.ShouldBeNull();
                continue;
            }

            var errors = RichContentRules.ValidateNodes([WithAncestors(kind, node)], "b");
            // A new link carries the editor's "https://" placeholder until the author types a host.
            errors.Where(e => !(kind == RichTextNodeKind.Link && e.Code == "unsafe-url")).ShouldBeEmpty(kind.ToString());
        }
    }

    [Fact]
    public void Create_Table_IsTwoByTwoWithHeaderRow()
    {
        var table = RichTextEditorOperations.Create(RichTextNodeKind.Table);

        table.Children.Length.ShouldBe(2);
        table.Children.ShouldAllBe(row => row.Children.Length == 2);
        table.Children[0].Children.ShouldAllBe(cell => cell.Header == true);
        table.Children[1].Children.ShouldAllBe(cell => cell.Header == false);
    }

    [Fact]
    public void AllowedChildren_MatchesValidator()
    {
        foreach (var parentKind in AllKinds)
        {
            var allowed = RichTextEditorOperations.AllowedChildren(parentKind);
            foreach (var childKind in AllKinds)
            {
                var parent = Valid(parentKind) with { Text = null, Children = [Valid(childKind)] };

                var errors = RichContentRules.ValidateNodes([WithAncestors(parentKind, parent)], "b");

                if (allowed.Contains(childKind))
                {
                    errors.ShouldBeEmpty($"{childKind} inside {parentKind}");
                }
                else
                {
                    errors.ShouldNotBeEmpty($"{childKind} inside {parentKind}");
                }
            }
        }
    }

    [Fact]
    public void AllowedChildren_OnlyExcludesWhatTheParentRuleOrLinkNestingRejects()
    {
        // Kinds the validator accepts under a parent (parent rule) must all be offered, except Link inside Link.
        foreach (var parentKind in AllKinds)
        {
            foreach (var childKind in AllKinds)
            {
                var parent = Valid(parentKind) with { Text = null, Children = [Valid(childKind)] };
                var errors = RichContentRules.ValidateNodes([WithAncestors(parentKind, parent)], "b");
                var parentRuleOk = !errors.Any(e => e.Code == "invalid-node-parent");
                var offered = RichTextEditorOperations.AllowedChildren(parentKind).Contains(childKind);
                if (parentRuleOk && !offered)
                {
                    (parentKind, childKind).ShouldBe((RichTextNodeKind.Link, RichTextNodeKind.Link));
                }
            }
        }
    }

    [Fact]
    public void Label_IsNonEmptyForEveryKind()
    {
        foreach (var kind in AllKinds)
        {
            RichTextEditorOperations.Label(kind).ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Nodes_AppendRemoveMove()
    {
        var a = new RichTextNode(RichTextNodeKind.Text, Text: "a");
        var b = new RichTextNode(RichTextNodeKind.Text, Text: "b");
        var c = new RichTextNode(RichTextNodeKind.Text, Text: "c");

        var list = RichTextEditorOperations.Append(RichTextEditorOperations.Append(default, a), b);
        list.ShouldBe([a, b]);

        RichTextEditorOperations.Append(list, c).ShouldBe([a, b, c]);
        RichTextEditorOperations.Remove(list, 0).ShouldBe([b]);
        RichTextEditorOperations.Remove(list, 5).ShouldBe(list);
        RichTextEditorOperations.Move(list, 0, 1).ShouldBe([b, a]);
    }

    [Fact]
    public void Move_ClampsAtEdges()
    {
        var a = new RichTextNode(RichTextNodeKind.Text, Text: "a");
        var b = new RichTextNode(RichTextNodeKind.Text, Text: "b");
        var c = new RichTextNode(RichTextNodeKind.Text, Text: "c");
        ImmutableArray<RichTextNode> list = [a, b, c];

        RichTextEditorOperations.Move(list, 0, -1).ShouldBe(list);
        RichTextEditorOperations.Move(list, 2, 1).ShouldBe(list);
        RichTextEditorOperations.Move(list, 0, 99).ShouldBe([b, c, a]);
        RichTextEditorOperations.Move(list, 2, -99).ShouldBe([c, a, b]);

        var doc = Doc(new RichTextBlock("x", []), new RichTextBlock("y", []));
        ContentDocumentOperations.Move(doc, "x", -1).ShouldBe(doc);
        ContentDocumentOperations.Move(doc, "y", 1).ShouldBe(doc);
        ContentDocumentOperations.Move(doc, "x", 5).Blocks.Select(x => x.Id).ShouldBe(["y", "x"]);
    }

    [Fact]
    public void Remove_LastBlock_LeavesEmptyDocument()
    {
        var doc = Doc(new RichTextBlock("only", [Para("x")]));

        var result = ContentDocumentOperations.Remove(doc, "only");

        result.Blocks.Length.ShouldBe(1);
        var block = result.Blocks[0].ShouldBeOfType<RichTextBlock>();
        block.Nodes.ShouldBeEmpty();
        block.Id.ShouldNotBe("only");
        RichContentRules.Validate(result).ShouldBeEmpty();
    }

    [Fact]
    public void Add_Remove_Replace_Work()
    {
        var doc = Doc(new RichTextBlock("a", []), new RichTextBlock("b", []));

        ContentDocumentOperations.Remove(doc, "a").Blocks.Select(x => x.Id).ShouldBe(["b"]);
        ContentDocumentOperations.Remove(doc, "nope").ShouldBe(doc);

        ContentDocumentOperations.Add(doc, 1, new CtaBlock("c", "H", null, "L", "/x")).Blocks.Select(x => x.Id).ShouldBe(["a", "c", "b"]);
        ContentDocumentOperations.Add(doc, 99, new RichTextBlock("z", [])).Blocks.Select(x => x.Id).ShouldBe(["a", "b", "z"]);
        ContentDocumentOperations.Add(doc, -4, new RichTextBlock("z", [])).Blocks[0].Id.ShouldBe("z");

        ContentDocumentOperations.Replace(doc, new CtaBlock("b", "H", null, "L", "/x")).Blocks[1].ShouldBeOfType<CtaBlock>();
        ContentDocumentOperations.Replace(doc, new CtaBlock("missing", "H", null, "L", "/x")).ShouldBe(doc);
    }

    [Fact]
    public void SplitRichText_InsertsBlockBetweenTwoHalves()
    {
        var nodes = Enumerable.Range(0, 5).Select(i => Para(i.ToString())).ToImmutableArray();
        var doc = Doc(new RichTextBlock("art", nodes));
        var cta = new CtaBlock("cta", "H", null, "L", "/x");

        var result = ContentDocumentOperations.SplitRichText(doc, "art", 2, cta);

        result.Blocks.Length.ShouldBe(3);
        var first = result.Blocks[0].ShouldBeOfType<RichTextBlock>();
        first.Id.ShouldBe("art");
        first.Nodes.ShouldBe(nodes[..2]);
        result.Blocks[1].ShouldBe(cta);
        var second = result.Blocks[2].ShouldBeOfType<RichTextBlock>();
        second.Id.ShouldNotBe("art");
        second.Nodes.ShouldBe(nodes[2..]);
        RichContentRules.Validate(result).ShouldBeEmpty();
    }

    [Fact]
    public void SplitRichText_AtEdges_DropsEmptyHalf_AndIgnoresNonRichText()
    {
        var doc = Doc(new RichTextBlock("art", [Para("a"), Para("b")]), new CtaBlock("cta", "H", null, "L", "/x"));
        var insert = new CtaBlock("new", "H", null, "L", "/x");

        var atStart = ContentDocumentOperations.SplitRichText(doc, "art", 0, insert);
        atStart.Blocks.Length.ShouldBe(3);
        atStart.Blocks[0].ShouldBe(insert);
        atStart.Blocks[1].ShouldBeOfType<RichTextBlock>().Nodes.Length.ShouldBe(2);

        ContentDocumentOperations.SplitRichText(doc, "art", 2, insert).Blocks.Select(b => b.Id).ShouldBe(["art", "new", "cta"]);
        ContentDocumentOperations.SplitRichText(doc, "cta", 0, insert).ShouldBe(doc);
        ContentDocumentOperations.SplitRichText(doc, "missing", 0, insert).ShouldBe(doc);
    }

    [Fact]
    public void UnknownBlocks_SurviveEveryOperation()
    {
        using var raw = JsonDocument.Parse("""{"$block":"future","id":"u1","x":1}""");
        var unknown = new UnknownBlock("u1", raw.RootElement.Clone());
        var doc = Doc(new RichTextBlock("a", [Para("x"), Para("y")]), unknown);

        ContentDocumentOperations.Add(doc, 0, new RichTextBlock("n", [])).Blocks.ShouldContain(unknown);
        ContentDocumentOperations.Replace(doc, new RichTextBlock("a", [])).Blocks.ShouldContain(unknown);
        ContentDocumentOperations.Remove(doc, "a").Blocks.ShouldContain(unknown);
        ContentDocumentOperations.Move(doc, "a", 1).Blocks.ShouldContain(unknown);
        ContentDocumentOperations.Move(doc, "u1", -1).Blocks.ShouldContain(unknown);
        ContentDocumentOperations.SplitRichText(doc, "a", 1, new CtaBlock("c", "H", null, "L", "/x")).Blocks.ShouldContain(unknown);
        ContentDocumentOperations.Replace(doc, unknown).Blocks.ShouldContain(unknown);
    }

    [Fact]
    public void NewBlockId_MatchesIdPattern()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => ContentDocumentOperations.NewBlockId()).ToList();

        ids.ShouldAllBe(id => Regex.IsMatch(id, "^b-[0-9a-z]{8}$"));
        ids.Distinct().Count().ShouldBe(ids.Count);
        RichContentRules.Validate(Doc([.. ids.Select(id => (ContentBlock)new RichTextBlock(id, []))])).ShouldBeEmpty();
    }

    private static ContentDocument Doc(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    private static RichTextNode Para(string text) =>
        new(RichTextNodeKind.Paragraph, Children: [new(RichTextNodeKind.Text, Text: text)]);
}
