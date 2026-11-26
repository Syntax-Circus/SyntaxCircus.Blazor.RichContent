using System.Collections.Immutable;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

/// <summary>What Write mode shows for a document, and what its cards, empty places and Insert do on the .NET side.</summary>
public sealed class WritingLayoutTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly BunitJSModuleInterop _module;
    private ContentDocument? _stored;
    private int _changes;

    public WritingLayoutTests()
    {
        _context.Services.AddRichContent();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _context.JSInterop.SetupModule(RichTextSurface.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        for (var generation = 1; generation <= 20; generation++)
        {
            var held = generation;
            _module.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == held).SetResult(held);
        }
    }

    public void Dispose() => _context.Dispose();

    private static RichTextNode Text(string text) => new(Kind.Text, Text: text);

    private static RichTextNode Paragraph(string text) => new(Kind.Paragraph, Children: [Text(text)]);

    private static RichTextNode CodeNode(string text = "x = 1") => new(Kind.CodeBlock, Text: text);

    private static RichTextNode ImageNode(string alt = "alt") => new(Kind.Image, Media: new MediaRef("mountains"), Alt: alt);

    private static RichTextNode Cell(string text, bool? header = false) => new(Kind.TableCell, Header: header, Children: [Text(text)]);

    private static RichTextNode Row(params RichTextNode[] cells) => new(Kind.TableRow, Children: [.. cells]);

    private static RichTextNode TableNode(params RichTextNode[] rows) => new(Kind.Table, Children: [.. rows]);

    private static RichTextNode Locked() => Paragraph("kept as it is") with
    {
        Extra = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.SerializeToElement(1) },
    };

    private static ContentDocument Document(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    private static RichTextBlock Rich(string id, params RichTextNode[] nodes) => new(id, [.. nodes]);

    private static CalloutBlock Callout(string id, params RichTextNode[] nodes) => new(id, CalloutTone.Info, null, [.. nodes]);

    private IRenderedComponent<ContentDocumentEditor> Render(ContentDocument document, bool disabled = false) =>
        _context.Render<ContentDocumentEditor>(parameters => parameters
            .Add(editor => editor.Document, document)
            .Add(editor => editor.Mode, RichContentEditorMode.Write)
            .Add(editor => editor.Disabled, disabled)
            .Add(editor => editor.DocumentChanged, changed =>
            {
                _stored = changed;
                _changes++;
            }));

    // The writing area's own children, in order: "surface", or the kind of card.
    private static string[] Shown(IRenderedComponent<ContentDocumentEditor> editor, string blockId) =>
        [.. editor.Find($"[data-block-id='{blockId}'] .rt-writing").Children.Select(child => child.GetAttribute("data-rt-card") ?? "surface")];

    [Fact]
    public void Segments_RenderTheRightComponents()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"), CodeNode(), ImageNode(), TableNode(Row(Cell("c"))), Locked())));

        Shown(editor, "one").ShouldBe(["surface", "code", "surface", "image", "surface", "table", "surface", "locked", "surface"]);
        editor.FindAll("[data-rt-card=code] textarea[data-rt-code]").Count.ShouldBe(1);
        editor.FindAll("[data-rt-card=code] select").Count.ShouldBe(1);
        editor.FindAll("[data-rt-card=image] input[type=text]").Count.ShouldBe(2);
        editor.FindAll("[data-rt-card=table] [data-rt-surface=inline]").Count.ShouldBe(1);
        _changes.ShouldBe(0);
    }

    [Fact]
    public void ABlockThatStartsAndEndsWithACard_HasAPlaceBeforeAndAfter()
    {
        var editor = Render(Document(Rich("one", CodeNode())));

        Shown(editor, "one").ShouldBe(["surface", "code", "surface"]);
        editor.FindAll("[data-rt-place]").ShouldBeEmpty("a rich text block's own surfaces are the places next to it");
    }

    [Fact]
    public void AnEmptyPlace_IsOfferedWhereverNoRichTextBlockAdjoins()
    {
        var editor = Render(Document(Callout("a", Paragraph("a")), Callout("b", Paragraph("b")), Rich("r", Paragraph("r")), Callout("c", Paragraph("c"))));

        var order = editor.FindAll("[data-rt-editor] > section")
            .Select(section => section.HasAttribute("data-rt-place") ? "place" : section.GetAttribute("data-block-id"));

        order.ShouldBe(["place", "a", "place", "b", "r", "c", "place"]);
        _changes.ShouldBe(0);
    }

    [Fact]
    public async Task AnEmptyPlace_BecomingARealBlock_KeepsItsSurface()
    {
        var editor = Render(Document(Callout("a", Paragraph("a")), Callout("b", Paragraph("b"))));
        editor.FindAll("[data-rt-editor] > section")[2].HasAttribute("data-rt-place").ShouldBeTrue();
        // In page order: place, a's body, place, b's body, place.
        var place = editor.FindComponents<RichTextSurface>()[2].Instance;
        var patch = """{"generation":1,"order":["c1"],"changed":[{"key":"c1","node":{"kind":"paragraph","children":[{"kind":"text","text":"between"}]}}]}""";

        (await editor.InvokeAsync(() => place.ReceiveEdit(1, 0, 1, patch))).ShouldBeTrue();
        editor.Render(parameters => parameters.Add(e => e.Document, _stored!));

        _stored!.Blocks.Length.ShouldBe(3);
        var made = _stored.Blocks[1].ShouldBeOfType<RichTextBlock>();
        made.Nodes.ShouldHaveSingleItem().Children[0].Text.ShouldBe("between");
        editor.FindComponents<RichTextSurface>().Select(surface => surface.Instance).ShouldContain(place);
        Shown(editor, made.Id).ShouldBe(["surface"]);
    }

    [Fact]
    public void LockedCard_ShowsText_NoInputs_MoveAndRemoveOnly()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"), Locked())));

        var card = editor.Find("[data-rt-card=locked]");

        card.TextContent.ShouldContain("kept as it is");
        card.QuerySelectorAll("input, select, textarea, [contenteditable], [data-rt-surface]").Length.ShouldBe(0);
        card.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-label")).ShouldBe(["Move up", "Move down", "Remove"]);
    }

    private static Task ReportAsync(IRenderedComponent<ContentDocumentEditor> editor, RichTextSurface surface) =>
        editor.InvokeAsync(() => surface.SelectionChanged(new SurfaceSelection(false, false, false, null, "p", "none", false, false)));

    private static AngleSharp.Dom.IElement InsertButton(IRenderedComponent<ContentDocumentEditor> editor) =>
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Insert");

    private static string[] InsertChoices(IRenderedComponent<ContentDocumentEditor> editor) =>
        [.. editor.FindAll("[role=group][aria-label=Insert] button").Select(button => button.TextContent)];

    [Fact]
    public async Task CalloutInsert_OffersOnlyCodeImageTable()
    {
        var editor = Render(Document(Rich("one", Paragraph("a")), Callout("a", Paragraph("inside"))));
        var surfaces = editor.FindComponents<RichTextSurface>();

        await ReportAsync(editor, surfaces[1].Instance);
        InsertButton(editor).Click();

        InsertChoices(editor).ShouldBe(["Code block", "Image", "Table"]);

        await ReportAsync(editor, surfaces[0].Instance);
        InsertButton(editor).Click();

        InsertChoices(editor).ShouldBe(["Code block", "Image", "Table", "Gallery", "Callout", "Call to action"]);
    }

    [Fact]
    public async Task TableCell_InsertDisabled()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"), TableNode(Row(Cell("c"))))));
        var cell = editor.FindComponents<RichTextSurface>().Single(surface => surface.Instance.Profile == RichTextSurfaceProfile.Inline);
        InsertButton(editor).HasAttribute("disabled").ShouldBeTrue("nothing has the cursor");

        await ReportAsync(editor, cell.Instance);

        InsertButton(editor).HasAttribute("disabled").ShouldBeTrue();
        editor.Find("[data-rt-command=bold]").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public async Task ASurfaceTheAuthorLeft_NoLongerHasTheToolbar()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"))));
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        await ReportAsync(editor, surface);
        InsertButton(editor).Click();

        await editor.InvokeAsync(surface.SurfaceLeft);

        editor.FindAll("[role=toolbar] button, [role=toolbar] select").ShouldAllBe(control => control.HasAttribute("disabled"));
        editor.FindAll("[role=group][aria-label=Insert]").ShouldBeEmpty();
    }

    [Fact]
    public void SetTableHeaderRow_TogglesFirstRowOnly()
    {
        var extra = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.SerializeToElement(1) };
        var second = Row(Cell("c", header: null), Cell("d", header: true));
        var table = TableNode(Row(Cell("a"), Cell("b", header: null)), second) with { Extra = extra };

        var on = RichTextEditorOperations.SetTableHeaderRow(table, true);
        var off = RichTextEditorOperations.SetTableHeaderRow(on, false);

        on.Children[0].Children.ShouldAllBe(cell => cell.Header == true);
        on.Children[1].ShouldBeSameAs(second);
        on.Extra.ShouldBeSameAs(extra);
        on.Children[0].Children[0].Children.ShouldBe(table.Children[0].Children[0].Children);
        off.Children[0].Children.ShouldAllBe(cell => cell.Header == false);
        off.Children[1].ShouldBeSameAs(second);
        RichTextEditorOperations.SetTableHeaderRow(new RichTextNode(Kind.Table, Children: []), true).Children.ShouldBeEmpty();
    }

    private static AngleSharp.Dom.IElement CardButton(IRenderedComponent<ContentDocumentEditor> editor, string card, string name) =>
        editor.Find($"[data-rt-card={card}] button[aria-label='{name}']");

    private static AngleSharp.Dom.IElement ConfirmButton(IRenderedComponent<ContentDocumentEditor> editor, string text) =>
        editor.FindAll("[data-rt-confirm] button").Single(button => button.TextContent == text);

    private string[] StoredKinds(int block = 0) => [.. ((RichTextBlock)_stored!.Blocks[block]).Nodes.Select(node => node.Kind.ToString())];

    [Theory]
    [InlineData("code")]
    [InlineData("image")]
    [InlineData("table")]
    [InlineData("locked")]
    public void RemovingNonEmptyCard_Asks_EmptyDoesNot(string kind)
    {
        var full = kind switch
        {
            "code" => CodeNode(),
            "image" => ImageNode(),
            "table" => TableNode(Row(Cell(""), Cell("x"))),
            _ => Locked(),
        };
        var editor = Render(Document(Rich("one", Paragraph("a"), full)));

        CardButton(editor, kind, "Remove").Click();

        _changes.ShouldBe(0);
        editor.Find("[data-rt-confirm]").TextContent.ShouldContain("Remove?");
        ConfirmButton(editor, "No").Click();
        editor.FindAll("[data-rt-confirm]").ShouldBeEmpty();
        _changes.ShouldBe(0);
        CardButton(editor, kind, "Remove").Click();
        ConfirmButton(editor, "Yes").Click();
        StoredKinds().ShouldBe(["Paragraph"]);

        if (kind == "locked")
        {
            return;
        }

        // The same card with nothing in it goes at once.
        var empty = RichTextEditorOperations.Create(kind switch
        {
            "code" => Kind.CodeBlock,
            "image" => Kind.Image,
            _ => Kind.Table,
        });
        editor.Render(parameters => parameters.Add(e => e.Document, Document(Rich("one", Paragraph("a"), empty))));

        CardButton(editor, kind, "Remove").Click();

        editor.FindAll("[data-rt-confirm]").ShouldBeEmpty();
        StoredKinds().ShouldBe(["Paragraph"]);
    }

    private static AngleSharp.Dom.IElement BlockRemove(IRenderedComponent<ContentDocumentEditor> editor, string id) =>
        editor.FindAll($"[data-block-id='{id}'] > header button").Single(button => button.TextContent == "Remove");

    [Fact]
    public void RemovingANonEmptyBlockCard_Asks_AndAnEmptyOneDoesNot()
    {
        var editor = Render(Document(
            Rich("r", Paragraph("r")),
            Callout("full", Paragraph("something")),
            Callout("empty", RichTextEditorOperations.Create(Kind.Paragraph)),
            new CtaBlock("cta", "", null, "", "https://")));

        BlockRemove(editor, "full").Click();
        _changes.ShouldBe(0);
        ConfirmButton(editor, "Yes").Click();
        _stored!.Blocks.Select(block => block.Id).ShouldBe(["r", "empty", "cta"]);
        editor.Render(parameters => parameters.Add(e => e.Document, _stored));

        BlockRemove(editor, "empty").Click();
        _stored.Blocks.Select(block => block.Id).ShouldBe(["r", "cta"]);
        editor.Render(parameters => parameters.Add(e => e.Document, _stored));
        BlockRemove(editor, "cta").Click();
        _stored.Blocks.Select(block => block.Id).ShouldBe(["r"]);
    }

    [Fact]
    public void MovingACard_MovesItOneNode()
    {
        var code = CodeNode();
        var editor = Render(Document(Rich("one", Paragraph("a"), Paragraph("b"), code)));

        CardButton(editor, "code", "Move up").Click();

        StoredKinds().ShouldBe(["Paragraph", "CodeBlock", "Paragraph"]);
        ((RichTextBlock)_stored!.Blocks[0]).Nodes[1].ShouldBeSameAs(code);
        Shown(editor, "one").ShouldBe(["surface", "code", "surface"]);
    }

    // The keys the browser was given by the nth redraw.
    private string[] Keys(int redraw) =>
    [
        .. ((IReadOnlyList<KeyedNode>)_module.Invocations["sync"].Where(call => (bool)call.Arguments[3]!).ElementAt(redraw).Arguments[1]!)
            .Select(node => node.Key),
    ];

    [Theory]
    // After the first of two paragraphs, at the start, and at the end.
    [InlineData(1, new[] { "Paragraph", "CodeBlock", "Paragraph" })]
    [InlineData(0, new[] { "CodeBlock", "Paragraph", "Paragraph" })]
    [InlineData(2, new[] { "Paragraph", "Paragraph", "CodeBlock" })]
    public async Task Insert_GoesAfterTheNodeTheBrowserNames(int after, string[] expected)
    {
        var first = Paragraph("a");
        var second = Paragraph("b");
        var editor = Render(Document(Rich("one", first, second)));
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        var keys = Keys(0);

        await editor.InvokeAsync(() => surface.InsertRequested(0, after == 0 ? null : keys[after - 1], 0));

        StoredKinds().ShouldBe(expected);
        ((RichTextBlock)_stored!.Blocks[0]).Nodes.Where(node => node.Kind == Kind.Paragraph).ShouldBe([first, second]);
    }

    [Fact]
    public async Task InsertingABlock_SplitsTheTextBlock()
    {
        var first = Paragraph("a");
        var second = Paragraph("b");
        var editor = Render(Document(Rich("one", first, second)));
        var surface = editor.FindComponent<RichTextSurface>().Instance;

        // Choice 4 is the callout: code, image, table, gallery, callout.
        await editor.InvokeAsync(() => surface.InsertRequested(4, Keys(0)[0], 0));

        _stored!.Blocks.Select(block => block.GetType()).ShouldBe([typeof(RichTextBlock), typeof(CalloutBlock), typeof(RichTextBlock)]);
        _stored.Blocks[0].Id.ShouldBe("one");
        ((RichTextBlock)_stored.Blocks[0]).Nodes.ShouldBe([first]);
        ((RichTextBlock)_stored.Blocks[2]).Nodes.ShouldBe([second]);
    }

    [Theory]
    // A patch the browser sent that never got here, a key nobody has, and a choice that does not exist or is blocked.
    [InlineData(0, null, 7)]
    [InlineData(0, "nope", 0)]
    [InlineData(99, null, 0)]
    [InlineData(1, null, 0)]
    public async Task AnInsertThatCannotBeTrusted_ChangesNothing(int choice, string? after, long sent)
    {
        var editor = Render(Document(Rich("one", Paragraph("a"))));
        var surface = editor.FindComponent<RichTextSurface>().Instance;

        await editor.InvokeAsync(() => surface.InsertRequested(choice, after, sent));

        _changes.ShouldBe(0);
    }

    // A patch that gives one of the surface's nodes other text and keeps all the others.
    private static string Retype(string[] keys, int node, string text) => JsonSerializer.Serialize(
        new SurfacePatch(1, keys, [new KeyedNode(keys[node], new SurfaceNode("paragraph", Children: [new SurfaceNode("text", Text: text)]))]),
        JsonSerializerOptions.Web);

    [Theory]
    // Which surface is typed into, and which of its nodes. Each action rewrites that surface and moves the node.
    [InlineData("insert", 0, 1, new[] { "a", "code", "b", "code", "c" })]
    [InlineData("insert-block", 0, 1, new[] { "a" })]
    [InlineData("move", 0, 1, new[] { "a", "code", "b", "c" })]
    [InlineData("remove", 1, 0, new[] { "a", "b", "c" })]
    public async Task WhatAnOldSurfaceSends_BeforeItHasBeenDrawnAway_IsNeitherStoredNorAcknowledged(
        string action, int surfaceIndex, int node, string[] expected)
    {
        // Called by the host while it takes the rearranged document: nothing has been drawn again yet, as when
        // Blazor holds renders back, and the old surface is still there.
        Func<Task>? whileUndrawn = null;
        var editor = _context.Render<ContentDocumentEditor>(parameters => parameters
            .Add(e => e.Document, Document(Rich("one", Paragraph("a"), Paragraph("b"), CodeNode("code"), Paragraph("c"))))
            .Add(e => e.Mode, RichContentEditorMode.Write)
            .Add(e => e.DocumentChanged, changed =>
            {
                _stored = changed;
                _changes++;
                var probe = whileUndrawn;
                whileUndrawn = null;
                probe?.Invoke();
            }));
        var old = editor.FindComponents<RichTextSurface>()[surfaceIndex].Instance;
        var keys = Keys(surfaceIndex);
        bool? acknowledged = null;
        whileUndrawn = () =>
        {
            var answer = old.ReceiveEdit(1, 0, 1, Retype(keys, node, "typed"));
            acknowledged = answer.IsCompletedSuccessfully && answer.Result;
            return Task.CompletedTask;
        };

        switch (action)
        {
            case "insert":
                await editor.InvokeAsync(() => old.InsertRequested(0, keys[0], 0));
                break;
            case "insert-block":
                await editor.InvokeAsync(() => old.InsertRequested(4, keys[0], 0));
                break;
            case "move":
                CardButton(editor, "code", "Move up").Click();
                break;
            default:
                CardButton(editor, "code", "Remove").Click();
                ConfirmButton(editor, "Yes").Click();
                break;
        }

        acknowledged.ShouldBe(false, "the browser must not take it to be saved");
        _changes.ShouldBe(1, "nothing of it reaches the document");
        ((RichTextBlock)_stored!.Blocks[0]).Nodes
            .Select(n => n.Kind == Kind.CodeBlock ? (n.Text == "" ? "code" : n.Text) : n.Children[0].Text)
            .ShouldBe(expected, "no node is duplicated, restored or lost");
    }

    [Fact]
    public async Task AnEditSentToADisposedComponent_IsNotAcknowledged()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"))));
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        var keys = Keys(0);
        await editor.InvokeAsync(async () => await surface.DisposeAsync());

        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, Retype(keys, 0, "late")))).ShouldBeFalse();

        _changes.ShouldBe(0);
    }

    private static CtaBlock Cta() => new("cta", "Heading", "Body", "Label", "https://example.com/");

    private static GalleryBlock Gallery() => new("gallery", [new GalleryImage(new MediaRef("mountains"), "Alt", null)], "Title");

    // The host keeps what it is given but passes nothing back down, as while renders are held back: each field's
    // handler was drawn for the block as it was.
    private static void TypeInto(IRenderedComponent<ContentDocumentEditor> editor, string block, int field, string text) =>
        editor.FindAll($"[data-block-id={block}] input[type=text], [data-block-id={block}] textarea")[field].Input(text);

    [Fact]
    public void ACallToActionInWriteMode_BuildsEachEditOnTheLast_AndAReplacementWins()
    {
        var editor = Render(Document(Rich("one", Paragraph("a")), Cta()));

        TypeInto(editor, "cta", 0, "New heading");
        TypeInto(editor, "cta", 1, "New body");
        TypeInto(editor, "cta", 2, "New label");
        TypeInto(editor, "cta", 3, "/new");

        _stored!.Blocks[1].ShouldBe(new CtaBlock("cta", "New heading", "New body", "New label", "/new"));

        var replaced = Cta() with { Heading = "From the host" };
        editor.Render(parameters => parameters.Add(e => e.Document, Document(Rich("one", Paragraph("a")), replaced)));
        TypeInto(editor, "cta", 2, "After");

        _stored!.Blocks[1].ShouldBe(replaced with { Label = "After" });
    }

    [Fact]
    public void AGalleryInWriteMode_BuildsEachEditOnTheLast_AndAReplacementWins()
    {
        var editor = Render(Document(Rich("one", Paragraph("a")), Gallery()));

        TypeInto(editor, "gallery", 0, "New title");
        TypeInto(editor, "gallery", 1, "New alt");
        TypeInto(editor, "gallery", 2, "New caption");

        var stored = (GalleryBlock)_stored!.Blocks[1];
        stored.Title.ShouldBe("New title");
        stored.Images.ShouldHaveSingleItem().ShouldBe(new GalleryImage(new MediaRef("mountains"), "New alt", "New caption"));

        var replaced = Gallery() with { Title = "From the host" };
        editor.Render(parameters => parameters.Add(e => e.Document, Document(Rich("one", Paragraph("a")), replaced)));
        TypeInto(editor, "gallery", 1, "After");

        stored = (GalleryBlock)_stored!.Blocks[1];
        stored.Title.ShouldBe("From the host");
        stored.Images.ShouldHaveSingleItem().Alt.ShouldBe("After");
        stored.Images[0].Caption.ShouldBeNull();
    }

    [Fact]
    public void ACallToActionInBlocksMode_ComposesOnItsParameter_AsBefore()
    {
        ContentDocument? stored = null;
        var editor = _context.Render<ContentDocumentEditor>(parameters => parameters
            .Add(e => e.Document, Document(Cta()))
            .Add(e => e.DocumentChanged, changed => stored = changed));

        TypeInto(editor, "cta", 0, "New heading");
        TypeInto(editor, "cta", 2, "New label");

        stored!.Blocks[0].ShouldBe(Cta() with { Label = "New label" });
    }

    private static Dictionary<string, JsonElement> Future(string name) =>
        new() { [name] = JsonSerializer.SerializeToElement(new { kept = name }) };

    private static string Json(RichTextNode node) => JsonSerializer.Serialize(node, ContentDocumentSerializer.Options);

    [Fact]
    public async Task CodeImageAndTableNodesWithUnknownProperties_AreLocked_AndStayByteIdentical()
    {
        var cell = Cell("c") with { Extra = Future("onCell") };
        RichTextNode[] unknown =
        [
            CodeNode("kept code") with { Extra = Future("onCode") },
            ImageNode("kept image") with { Extra = Future("onImage") },
            TableNode(Row(Cell("a"))) with { Extra = Future("onTable") },
            TableNode(Row(Cell("b")) with { Extra = Future("onRow") }),
            TableNode(Row(Cell("a"), cell)),
        ];
        var before = unknown.Select(Json).ToArray();
        var editor = Render(Document(Rich("one", [Paragraph("first"), .. unknown, CodeNode("plain"), Paragraph("last")])));
        void ShouldBeKept() => ((RichTextBlock)_stored!.Blocks[0]).Nodes
            .Where(n => unknown.Contains(n, ReferenceEqualityComparer.Instance)).Select(Json).Order().ShouldBe(before.Order());
        void ShouldKeepDrawing() => editor.Render(parameters => parameters.Add(e => e.Document, _stored!));

        editor.FindAll("[data-rt-card=locked]").Count.ShouldBe(unknown.Length);
        editor.FindAll("[data-rt-card=table], [data-rt-card=image]").ShouldBeEmpty();
        editor.FindAll("[data-rt-card=locked] input, [data-rt-card=locked] textarea, [data-rt-card=locked] [data-rt-surface]")
            .ShouldBeEmpty();

        // A neighbouring edit.
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, Retype(Keys(0), 0, "first, edited")));
        ShouldBeKept();
        ShouldKeepDrawing();

        // Insert beside the first of them, through the surface before it.
        var first = editor.FindComponents<RichTextSurface>()[0].Instance;
        await editor.InvokeAsync(() => first.InsertRequested(2, Keys(0)[0], 1));
        ShouldBeKept();
        ShouldKeepDrawing();

        // Move each of them, then remove a neighbour.
        for (var i = 0; i < unknown.Length; i++)
        {
            editor.FindAll("[data-rt-card=locked] button[aria-label='Move down']")[i].Click();
            ShouldBeKept();
            ShouldKeepDrawing();
        }

        CardButton(editor, "code", "Remove").Click();
        ConfirmButton(editor, "Yes").Click();
        ShouldBeKept();
        ((RichTextBlock)_stored!.Blocks[0]).Nodes.Count(n => n.Kind == Kind.CodeBlock).ShouldBe(1);
    }

    [Fact]
    public async Task AnEmptiedTableCell_KeepsTheTextRunTheRulesAskFor()
    {
        var editor = Render(Document(Rich("one", TableNode(Row(Cell("c"))))));
        var cell = editor.FindComponents<RichTextSurface>().Single(surface => surface.Instance.Profile == RichTextSurfaceProfile.Inline).Instance;
        var before = _module.Invocations["sync"].Count(call => (bool)call.Arguments[3]!);

        await editor.InvokeAsync(() => cell.ReceiveEdit(1, 0, 1, """{"generation":1,"order":[],"changed":[]}"""));
        editor.Render(parameters => parameters.Add(e => e.Document, _stored!));

        RichContentRules.Validate(_stored!).ShouldBeEmpty();
        _module.Invocations["sync"].Count(call => (bool)call.Arguments[3]!).ShouldBe(before, "the cell is not drawn again");
    }

    // A code card's script, stood in for.
    private BunitJSModuleInterop CodeModule()
    {
        var module = _context.JSInterop.SetupModule(CodeCard.ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        return module;
    }

    private static string CodePatch(int generation, string? text)
    {
        var changed = text is null ? Array.Empty<object>() : [new { key = "code", node = text }];
        return JsonSerializer.Serialize(new { generation, order = (string[])["code"], changed }, JsonSerializerOptions.Web);
    }

    [Fact]
    public async Task ACodeCardsEdit_IsStoredBeforeReceiveEditFirstYields_AndTheLanguageComposesOnIt()
    {
        CodeModule().Setup<int?>("sync", _ => true).SetResult(1);
        var editor = Render(Document(Rich("one", CodeNode("a"))));
        var card = editor.FindComponent<CodeCard>().Instance;
        Task<bool>? received = null;

        await editor.InvokeAsync(() =>
        {
            received = card.ReceiveEdit(1, 0, 1, CodePatch(1, "ab"));
            ((RichTextBlock)_stored!.Blocks[0]).Nodes[0].Text.ShouldBe("ab");
        });

        (await received!).ShouldBeTrue();
        // The host has not passed the new document down, so the select was drawn for the old node.
        editor.Find("[data-rt-card=code] select").Change("sql");
        var stored = ((RichTextBlock)_stored!.Blocks[0]).Nodes[0];
        stored.Text.ShouldBe("ab");
        stored.Language.ShouldBe("sql");
        _changes.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"generation":1,"order":["code"]}""")]
    [InlineData("""{"generation":1,"order":["code"],"changed":[null]}""")]
    [InlineData("""{"generation":1,"order":["code"],"changed":[{"key":"code"}]}""")]
    [InlineData("""{"generation":9,"order":["code"],"changed":[]}""")]
    [InlineData("""{"order":["code"],"changed":[]}""")]
    [InlineData("not json")]
    public async Task AMalformedCodePatch_IsRefused(string payload)
    {
        CodeModule().Setup<int?>("sync", _ => true).SetResult(1);
        var editor = Render(Document(Rich("one", CodeNode("a"))));
        var card = editor.FindComponent<CodeCard>().Instance;

        (await editor.InvokeAsync(() => card.ReceiveEdit(1, 0, 1, payload))).ShouldBeFalse();

        _changes.ShouldBe(0);
        card.HasRejectedEdit.ShouldBeTrue();
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    [InlineData("hangs")]
    public async Task ACodeCardsLostRedraw_IsSentAgain_AndAnOlderEditIsNotAcknowledged(string outcome)
    {
        var module = CodeModule();
        module.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == 1).SetResult(1);
        var lost = module.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == 2);
        if (outcome == "throws")
        {
            lost.SetException(new JSException("lost"));
        }
        else if (outcome == "disconnected")
        {
            lost.SetException(new JSDisconnectedException("lost"));
        }
        else if (outcome == "cancelled")
        {
            lost.SetCanceled();
        }

        var editor = Render(Document(Rich("one", CodeNode("a"))));
        var card = editor.FindComponent<CodeCard>().Instance;
        editor.Render(parameters => parameters.Add(e => e.Document, Document(Rich("one", CodeNode("from the host")))));
        int Redraws() => module.Invocations["sync"].Count(call => (bool)call.Arguments[3]!);
        Redraws().ShouldBe(2);

        (await editor.InvokeAsync(() => card.ReceiveEdit(1, 0, 1, CodePatch(1, "typed on the old text"))))
            .ShouldBeFalse("the browser must keep the edit");

        Redraws().ShouldBe(3);
        module.Invocations["sync"][^1].Arguments[1].ShouldBe("from the host");
        _changes.ShouldBe(0, "the replacement wins");
        (await editor.InvokeAsync(() => card.ReceiveEdit(2, 0, 1, CodePatch(2, "typed on the new text")))).ShouldBeTrue();
        ((RichTextBlock)_stored!.Blocks[0]).Nodes[0].Text.ShouldBe("typed on the new text");
        _context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    public async Task ACodeCardsDisposalCallThatFails_DoesNotThrow(string outcome)
    {
        var module = CodeModule();
        module.Setup<int?>("sync", _ => true).SetResult(1);
        var handler = module.SetupVoid("dispose", _ => true);
        if (outcome == "cancelled")
        {
            handler.SetCanceled();
        }
        else if (outcome == "throws")
        {
            handler.SetException(new JSException("boom"));
        }
        else
        {
            handler.SetException(new JSDisconnectedException("gone"));
        }

        var editor = Render(Document(Rich("one", CodeNode())));
        var card = editor.FindComponent<CodeCard>().Instance;

        await editor.InvokeAsync(async () => await card.DisposeAsync());

        (await editor.InvokeAsync(() => card.ReceiveEdit(1, 0, 1, CodePatch(1, "late")))).ShouldBeFalse();
        _changes.ShouldBe(0);
        _context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task NothingRedrawsASurface_ThatShowsWhatCouldNotBeConverted()
    {
        var editor = Render(Document(Rich("one", Paragraph("a"), CodeNode(), Paragraph("b"))));
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        var keys = Keys(0);
        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, """{"generation":1,"order":["unknown"],"changed":[]}""")))
            .ShouldBeFalse();

        // Removing the card would join the two surfaces, and inserting would split this one. Both would redraw it.
        CardButton(editor, "code", "Remove").Click();
        ConfirmButton(editor, "Yes").Click();
        await editor.InvokeAsync(() => surface.InsertRequested(0, null, 0));
        await editor.InvokeAsync(() => surface.InsertRequested(4, keys[0], 0));

        _changes.ShouldBe(0);
        editor.FindAll("[data-rt-card=code]").Count.ShouldBe(1);
    }

    [Fact]
    public void AStaleBlockButton_ActsOnItsOwnBlock()
    {
        var editor = Render(Document(Rich("a", Paragraph("a")), Rich("b", Paragraph("b")), Rich("c", Paragraph("c"))));
        // The host does not pass the document down, so the buttons keep the positions they were drawn with.
        editor.Find("[data-block-id=a] > header button:nth-of-type(3)").Click();

        editor.Find("[data-block-id=b] > header button:nth-of-type(3)").Click();

        _stored!.Blocks.Select(block => block.Id).ShouldBe(["c"]);
    }

    [Fact]
    public void ADisabledEditor_MakesEveryCardReadOnly()
    {
        var editor = Render(
            Document(Rich("one", CodeNode(), ImageNode(), TableNode(Row(Cell("c"))), Locked()), Callout("c", Paragraph("x"))),
            disabled: true);

        editor.FindAll("[data-rt-card] input, [data-rt-card] select, [data-rt-card] button, .block > header button")
            .ShouldAllBe(control => control.HasAttribute("disabled"));
        editor.Find("textarea[data-rt-code]").HasAttribute("readonly").ShouldBeTrue();
    }
}
