using System.Collections.Immutable;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

/// <summary>Write mode on the .NET side, with the surface script stood in for.</summary>
public sealed class WriteModeTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly BunitJSModuleInterop _module;

    public WriteModeTests()
    {
        _context.Services.AddRichContent();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _context.JSInterop.SetupModule(RichTextSurface.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    private static RichTextNode Paragraph(string text) =>
        new(Kind.Paragraph, Children: [new RichTextNode(Kind.Text, Text: text)]);

    private static ContentDocument TwoBlocks() => new(ContentDocument.CurrentSchemaVersion,
    [
        new RichTextBlock("one", [Paragraph("first"), new RichTextNode(Kind.CodeBlock, Text: "code"), Paragraph("after the code")]),
        new RichTextBlock("two", [Paragraph("second")]),
    ]);

    // The forced syncs so far, oldest first: the keys the browser was given and their generation.
    private (string[] Keys, int Generation)[] Redraws() =>
    [
        .. _module.Invocations["sync"]
            .Where(invocation => (bool)invocation.Arguments[3]!)
            .Select(invocation => (
                ((IReadOnlyList<KeyedNode>)invocation.Arguments[1]!).Select(node => node.Key).ToArray(),
                (int)invocation.Arguments[4]!)),
    ];

    private static string Patch(int generation, string[] order, params (string Key, string Text)[] changed) =>
        JsonSerializer.Serialize(
            new SurfacePatch(
                generation,
                order,
                [.. changed.Select(c => new KeyedNode(c.Key, new SurfaceNode("paragraph", Children: [new SurfaceNode("text", Text: c.Text)])))]),
            JsonSerializerOptions.Web);

    private static string[] Texts(ContentDocument document, int block) =>
        [.. ((RichTextBlock)document.Blocks[block]).Nodes.Select(node => node.Text ?? string.Concat(node.Children.Select(c => c.Text)))];

    private IRenderedComponent<ContentDocumentEditor> Render(
        ContentDocument document, Func<ContentDocument, Task>? changed = null, RichContentEditorMode mode = RichContentEditorMode.Write) =>
        _context.Render<ContentDocumentEditor>(parameters =>
        {
            parameters.Add(editor => editor.Document, document).Add(editor => editor.Mode, mode);
            if (changed is not null)
            {
                parameters.Add(editor => editor.DocumentChanged, changed);
            }
        });

    [Fact]
    public void BlocksMode_LoadsNoScript()
    {
        var editor = Render(TwoBlocks(), mode: RichContentEditorMode.Blocks);

        editor.FindComponents<RichTextSurface>().ShouldBeEmpty();
        _context.JSInterop.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public void Default_IsBlocksMode() => new ContentDocumentEditor().Mode.ShouldBe(RichContentEditorMode.Blocks);

    [Fact]
    public void WriteMode_ShowsASurfaceForEachRunOfProse()
    {
        var editor = Render(TwoBlocks());

        editor.FindComponents<RichTextSurface>().Count.ShouldBe(3);
        editor.Find("[data-rt-card=code]").TextContent.ShouldContain("code");
        Redraws().Select(redraw => redraw.Keys.Length).ShouldBe([1, 1, 1]);
    }

    [Fact]
    public async Task AnEdit_ReachesTheHostBeforeReceiveEditFirstYields()
    {
        // A host that stores the document and then takes its time. Anything awaited on the way there would let a
        // later click be handled first.
        var gate = new TaskCompletionSource();
        ContentDocument? stored = null;
        var editor = Render(TwoBlocks(), async document =>
        {
            stored = document;
            await gate.Task;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (keys, generation) = Redraws()[0];

        Task<bool>? answer = null;
        await editor.InvokeAsync(() =>
        {
            answer = surface.ReceiveEdit(1, 0, 1, Patch(generation, keys, (keys[0], "typed")));

            stored.ShouldNotBeNull();
            Texts(stored, 0).ShouldBe(["typed", "code", "after the code"]);
        });

        gate.SetResult();
        (await answer!).ShouldBeTrue();
    }

    [Fact]
    public async Task Edits_BuildOnEachOther_BeforeTheHostPassesTheDocumentBack()
    {
        // This handler never passes the document back down, as when rendering has not caught up.
        ContentDocument? stored = null;
        var editor = Render(TwoBlocks(), document =>
        {
            stored = document;
            return Task.CompletedTask;
        });
        var surfaces = editor.FindComponents<RichTextSurface>().Select(s => s.Instance).ToArray();
        var redraws = Redraws();

        Task Send(int surface, long sequence, params string[] texts) => editor.InvokeAsync(() => surfaces[surface].ReceiveEdit(
            sequence, 0, 1,
            Patch(redraws[surface].Generation, [redraws[surface].Keys[0], .. texts.Skip(1).Select((_, i) => $"c{i}")],
                [(redraws[surface].Keys[0], texts[0]), .. texts.Skip(1).Select((text, i) => ($"c{i}", text))])));

        // A surface that grows moves the nodes after it.
        await Send(0, 1, "grown", "twice");
        await Send(1, 2, "after, edited");
        await Send(2, 3, "second, edited");

        Texts(stored!, 0).ShouldBe(["grown", "twice", "code", "after, edited"]);
        Texts(stored!, 1).ShouldBe(["second, edited"]);
        Redraws().Length.ShouldBe(3, "the author's own edits redraw nothing");
    }

    [Fact]
    public async Task APatchThatChangesNothing_RaisesNoChange()
    {
        var changes = 0;
        var editor = Render(TwoBlocks(), _ =>
        {
            changes++;
            return Task.CompletedTask;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (keys, generation) = Redraws()[0];

        var answer = await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, Patch(generation, keys)));

        answer.ShouldBeTrue();
        changes.ShouldBe(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"generation":1,"order":["nope"],"changed":[]}""")]
    [InlineData("""{"generation":99,"order":[],"changed":[]}""")]
    [InlineData("""{"generation":1,"order":[],"changed":"no"}""")]
    public async Task ARejectedPayload_ReturnsFalse_ChangesNothing_AndALaterEditApplies(string payload)
    {
        var changes = 0;
        var editor = Render(TwoBlocks(), _ =>
        {
            changes++;
            return Task.CompletedTask;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (keys, generation) = Redraws()[0];

        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, payload))).ShouldBeFalse();
        surface.HasRejectedEdit.ShouldBeTrue();
        (await editor.InvokeAsync(() => surface.ReceiveEdit(2, 1, 2, "a gap"))).ShouldBeFalse();
        (await editor.InvokeAsync(() => surface.ReceiveEdit(3, 0, 1, null!))).ShouldBeFalse();
        changes.ShouldBe(0);

        (await editor.InvokeAsync(() => surface.ReceiveEdit(4, 0, 1, Patch(generation, keys, (keys[0], "ok"))))).ShouldBeTrue();
        surface.HasRejectedEdit.ShouldBeFalse();
        changes.ShouldBe(1);
    }

    [Fact]
    public async Task APatchInChunks_IsAppliedWhenTheLastArrives()
    {
        var changes = 0;
        var editor = Render(TwoBlocks(), _ =>
        {
            changes++;
            return Task.CompletedTask;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (keys, generation) = Redraws()[0];
        var patch = Patch(generation, keys, (keys[0], "in two parts"));

        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 2, patch[..10]))).ShouldBeTrue();
        changes.ShouldBe(0);
        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 1, 2, patch[10..]))).ShouldBeTrue();
        changes.ShouldBe(1);
    }

    [Fact]
    public async Task HostReplacement_RedrawsOnlyWhatDiffers_AndDropsOlderPatches()
    {
        var changes = 0;
        var document = TwoBlocks();
        var editor = Render(document, _ =>
        {
            changes++;
            return Task.CompletedTask;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (keys, generation) = Redraws()[0];

        // Equal values in new objects leave every surface alone.
        editor.Render(parameters => parameters.Add(e => e.Document, TwoBlocks()));
        Redraws().Length.ShouldBe(3);

        var replaced = TwoBlocks();
        replaced = replaced with
        {
            Blocks = replaced.Blocks.SetItem(0, (RichTextBlock)replaced.Blocks[0] with
            {
                Nodes = ((RichTextBlock)replaced.Blocks[0]).Nodes.SetItem(0, Paragraph("from the host")),
            }),
        };
        editor.Render(parameters => parameters.Add(e => e.Document, replaced));

        Redraws().Length.ShouldBe(4);
        Redraws()[3].Generation.ShouldBe(generation + 1);
        // Sent before the browser heard of the replacement: dropped, and not an error.
        var late = await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, Patch(generation, keys, (keys[0], "too late"))));
        late.ShouldBeTrue();
        surface.HasRejectedEdit.ShouldBeFalse();
        changes.ShouldBe(0);
    }

    [Fact]
    public void Disabling_IsSynchronizedWithoutARedraw()
    {
        var document = TwoBlocks();
        var editor = Render(document);
        var before = _module.Invocations["sync"].Count;

        editor.Render(parameters => parameters.Add(e => e.Disabled, true));

        var added = _module.Invocations["sync"].Skip(before).ToArray();
        added.Length.ShouldBe(3);
        added.ShouldAllBe(call => (bool)call.Arguments[2]! && !(bool)call.Arguments[3]! && call.Arguments[1] == null);
    }

    [Fact]
    public void AnEmptyBlock_OffersOneSurfaceToTypeIn()
    {
        var editor = Render(ContentDocument.Empty("start"));

        editor.FindComponents<RichTextSurface>().ShouldHaveSingleItem();
        Redraws().ShouldHaveSingleItem().Keys.ShouldBeEmpty();
    }
}
