using System.Collections.Immutable;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
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
    private readonly ErrorLog _errors = new();

    public WriteModeTests()
    {
        _context.Services.AddRichContent();
        _context.Services.AddLogging(logging => logging.AddProvider(_errors));
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _context.JSInterop.SetupModule(RichTextSurface.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        // As the script does, a sync answers with the generation the browser then holds.
        for (var generation = 1; generation <= 20; generation++)
        {
            SyncOf(generation).SetResult(generation);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _errors.Dispose();
    }

    // How the syncs of one generation behave from now on. A handler given no outcome never completes.
    private JSRuntimeInvocationHandler<int?> SyncOf(int generation) =>
        _module.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == generation);

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public List<Exception?> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Entries.Add(exception);
            }
        }

        public void Dispose()
        {
        }
    }

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

    // The first surface of the first block gets different content from the host.
    private static ContentDocument WithFirstReplaced()
    {
        var document = TwoBlocks();
        var first = (RichTextBlock)document.Blocks[0];
        return document with { Blocks = document.Blocks.SetItem(0, first with { Nodes = first.Nodes.SetItem(0, Paragraph("from the host")) }) };
    }

    private void SetOutcome(int generation, string outcome)
    {
        var handler = SyncOf(generation);
        switch (outcome)
        {
            case "throws":
                handler.SetException(new JSException("the sync was lost"));
                break;
            case "disconnected":
                handler.SetException(new JSDisconnectedException("the sync was lost"));
                break;
            case "cancelled":
                handler.SetCanceled();
                break;
            case "not applied":
                // It returned, but the browser still holds the generation before.
                handler.SetResult(generation - 1);
                break;
            case "applied":
                handler.SetResult(generation);
                break;
            default:
                // "hangs": no outcome, ever.
                break;
        }
    }

    private bool Faulted => _context.Renderer.UnhandledException.IsCompleted;

    [Theory]
    [InlineData("throws")]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    [InlineData("hangs")]
    [InlineData("not applied")]
    public async Task ALostSync_IsSentAgain_AndAnOlderPatchIsNotAcknowledged(string outcome)
    {
        var changes = 0;
        var editor = Render(TwoBlocks(), _ =>
        {
            changes++;
            return Task.CompletedTask;
        });
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (oldKeys, oldGeneration) = Redraws()[0];
        SetOutcome(oldGeneration + 1, outcome);

        editor.Render(parameters => parameters.Add(e => e.Document, WithFirstReplaced()));
        Redraws().Length.ShouldBe(4);
        var lost = Redraws()[3];

        // The browser never heard of the replacement, so what the author types there is made against the old content.
        Task<bool> TypeOnTheOldContent(long sequence) =>
            editor.InvokeAsync(() => surface.ReceiveEdit(sequence, 0, 1, Patch(oldGeneration, oldKeys, (oldKeys[0], "typed"))));

        (await TypeOnTheOldContent(1)).ShouldBeFalse("the browser must keep the edit");
        surface.HasRejectedEdit.ShouldBeFalse("nothing is wrong with what was typed");
        changes.ShouldBe(0, "the replacement wins");
        Redraws().Length.ShouldBe(5);
        Redraws()[4].Generation.ShouldBe(lost.Generation);
        Redraws()[4].Keys.ShouldBe(lost.Keys);

        // This time it gets through. Until it has answered, each older patch sends it again.
        SetOutcome(lost.Generation, "applied");
        (await TypeOnTheOldContent(2)).ShouldBeFalse();
        Redraws().Length.ShouldBe(6);

        // Confirmed: anything older was simply in flight, and is dropped.
        (await TypeOnTheOldContent(3)).ShouldBeTrue();
        Redraws().Length.ShouldBe(6);
        (await editor.InvokeAsync(() => surface.ReceiveEdit(4, 0, 1, Patch(lost.Generation, lost.Keys, (lost.Keys[0], "on the new content")))))
            .ShouldBeTrue();
        changes.ShouldBe(1);
        Faulted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("throws", true)]
    [InlineData("disconnected", false)]
    [InlineData("cancelled", false)]
    [InlineData("not applied", false)]
    public void ALostSync_IsSentAgainWithTheNextParameters(string outcome, bool logged)
    {
        var editor = Render(TwoBlocks());
        SetOutcome(2, outcome);
        editor.Render(parameters => parameters.Add(e => e.Document, WithFirstReplaced()));
        Faulted.ShouldBeFalse();
        // A script that threw is a defect, and must be seen. A connection that went is not.
        (_errors.Entries.OfType<JSException>().Any()).ShouldBe(logged);
        var before = _module.Invocations["sync"].Count;
        SetOutcome(2, "applied");

        editor.Render(parameters => parameters.Add(e => e.Disabled, true));

        var again = _module.Invocations["sync"].Skip(before).Where(call => (bool)call.Arguments[3]!).ToArray();
        var resent = again.ShouldHaveSingleItem();
        resent.Arguments[2].ShouldBe(true, "with the current Disabled");
        resent.Arguments[4].ShouldBe(2);

        before = _module.Invocations["sync"].Count;
        editor.Render(parameters => parameters.Add(e => e.Disabled, false));
        _module.Invocations["sync"].Skip(before).ShouldAllBe(call => !(bool)call.Arguments[3]!);
    }

    [Fact]
    public async Task APatchOfTheNewGeneration_ShowsThatTheSyncArrived()
    {
        var editor = Render(TwoBlocks(), _ => Task.CompletedTask);
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var (oldKeys, oldGeneration) = Redraws()[0];
        SetOutcome(2, "hangs");
        editor.Render(parameters => parameters.Add(e => e.Document, WithFirstReplaced()));
        var (keys, generation) = Redraws()[3];

        (await editor.InvokeAsync(() => surface.ReceiveEdit(1, 0, 1, Patch(generation, keys, (keys[0], "new"))))).ShouldBeTrue();
        (await editor.InvokeAsync(() => surface.ReceiveEdit(2, 0, 1, Patch(oldGeneration, oldKeys)))).ShouldBeTrue();

        Redraws().Length.ShouldBe(4);
    }

    [Theory]
    [InlineData("throws", true)]
    [InlineData("disconnected", false)]
    [InlineData("cancelled", false)]
    public async Task ADisposalCallThatFails_DoesNotThrow(string outcome, bool logged)
    {
        var editor = Render(TwoBlocks());
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;
        var handler = _module.SetupVoid("dispose", _ => true);
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

        await editor.InvokeAsync(async () => await surface.DisposeAsync());

        (_errors.Entries.OfType<JSException>().Any()).ShouldBe(logged);
        Faulted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("attach")]
    [InlineData("first draw")]
    public void AScriptThatFailsBeforeTheFirstDraw_IsReported_AndNoLongerUsed(string step)
    {
        if (step == "attach")
        {
            _module.SetupVoid("attach", _ => true).SetException(new JSException("attach failed"));
        }
        else
        {
            SetOutcome(1, "throws");
        }

        var editor = Render(TwoBlocks());

        editor.FindAll(".rt-surface-failed").Count.ShouldBe(3);
        editor.FindAll("[data-rt-surface]").ShouldBeEmpty();
        _errors.Entries.OfType<JSException>().Count().ShouldBe(3);
        var syncs = _module.Invocations["sync"].Count;
        editor.Render(parameters => parameters.Add(e => e.Disabled, true));
        _module.Invocations["sync"].Count.ShouldBe(syncs);
        Faulted.ShouldBeFalse();
    }

    [Theory]
    // However far the surface got before it was disposed.
    [InlineData("attach threw")]
    [InlineData("attach was cancelled")]
    [InlineData("first draw threw")]
    public async Task ADisposalCallThatThrows_OnASurfaceThatNeverBecameReady_DoesNotThrow(string history)
    {
        switch (history)
        {
            case "attach threw":
                _module.SetupVoid("attach", _ => true).SetException(new JSException("attach failed"));
                break;
            case "attach was cancelled":
                _module.SetupVoid("attach", _ => true).SetCanceled();
                break;
            default:
                SetOutcome(1, "throws");
                break;
        }

        _module.SetupVoid("dispose", _ => true).SetException(new JSException("dispose failed"));
        var editor = Render(TwoBlocks());
        var surface = editor.FindComponents<RichTextSurface>()[0].Instance;

        await editor.InvokeAsync(async () => await surface.DisposeAsync());

        _errors.Entries.OfType<JSException>().Select(e => e.Message).ShouldContain("dispose failed");
        Faulted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("disconnected")]
    public void AnAttachThatDoesNotArrive_DoesNotFaultTheCircuit(string outcome)
    {
        var handler = _module.SetupVoid("attach", _ => true);
        if (outcome == "cancelled")
        {
            handler.SetCanceled();
        }
        else
        {
            handler.SetException(new JSDisconnectedException("gone"));
        }

        Render(TwoBlocks());

        Faulted.ShouldBeFalse();
    }

    private static RichTextNode Code() => new(Kind.CodeBlock, Text: "code");

    // A node a surface cannot edit, so it gets a card of its own.
    private static RichTextNode Locked() => Paragraph("locked") with
    {
        Extra = new Dictionary<string, JsonElement> { ["future"] = JsonSerializer.SerializeToElement(1) },
    };

    private static ContentDocument OneBlock(params RichTextNode[] nodes) =>
        new(ContentDocument.CurrentSchemaVersion, [new RichTextBlock("one", [.. nodes])]);

    private static RichTextSurface[] Surfaces(IRenderedComponent<ContentDocumentEditor> editor) =>
        [.. editor.FindComponents<RichTextSurface>().Select(surface => surface.Instance)];

    // The parts as they are on the page, in order: each surface by its text, each card by its kind.
    private static string[] Shown(IRenderedComponent<ContentDocumentEditor> editor) =>
    [
        .. editor.FindComponents<RichTextSurface>().Select(surface => surface.Instance).Any()
            ? editor.Find(".rt-writing").Children.Select(child => child.GetAttribute("data-rt-card") ?? "surface")
            : [],
    ];

    private static string[] Contents(IRenderedComponent<ContentDocumentEditor> editor) =>
        [.. Surfaces(editor).Select(surface => string.Join('|', surface.Nodes.Select(n => string.Concat(n.Children.Select(c => c.Text)))))];

    [Theory]
    // The host passes the same objects where nothing changed, or new ones, as after a reload.
    [InlineData(false)]
    [InlineData(true)]
    public void ALockedNodeInserted_LeavesUnchangedSurfacesAlone(bool newObjects)
    {
        RichTextNode[] Original() => [Paragraph("a"), Paragraph("b"), Code(), Paragraph("c"), Code(), Paragraph("d")];
        var nodes = Original();
        var editor = Render(OneBlock(nodes));
        var before = Surfaces(editor);
        var syncs = Redraws().Length;
        var after = newObjects ? Original() : nodes;

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock([after[0], Locked(), .. after[1..]])));

        var surfaces = Surfaces(editor);
        Contents(editor).ShouldBe(["a", "b", "c", "d"]);
        Shown(editor).ShouldBe(["surface", "locked", "surface", "code", "surface", "code", "surface"]);
        surfaces[2].ShouldBeSameAs(before[1]);
        surfaces[3].ShouldBeSameAs(before[2]);
        surfaces[0].ShouldBeSameAs(before[0]);
        surfaces.Distinct().Count().ShouldBe(4);
        // Only the two pieces of the split surface are drawn.
        (Redraws().Length - syncs).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALockedNodeRemoved_LeavesUnchangedSurfacesAlone(bool newObjects)
    {
        RichTextNode[] Original() => [Paragraph("a"), Paragraph("b"), Code(), Paragraph("c"), Code(), Paragraph("d")];
        var nodes = Original();
        var editor = Render(OneBlock([nodes[0], Locked(), .. nodes[1..]]));
        var before = Surfaces(editor);
        var syncs = Redraws().Length;
        var after = newObjects ? Original() : nodes;

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(after)));

        var surfaces = Surfaces(editor);
        Contents(editor).ShouldBe(["a|b", "c", "d"]);
        Shown(editor).ShouldBe(["surface", "code", "surface", "code", "surface"]);
        surfaces[1].ShouldBeSameAs(before[2]);
        surfaces[2].ShouldBeSameAs(before[3]);
        surfaces.Distinct().Count().ShouldBe(3);
        (Redraws().Length - syncs).ShouldBe(1);
    }

    [Fact]
    public void SurfacesWithEqualContent_AreEachUsedOnce()
    {
        RichTextNode[] Original() => [Paragraph("x"), Code(), Paragraph("x"), Code(), Paragraph("y")];
        var editor = Render(OneBlock(Original()));
        var before = Surfaces(editor);
        var syncs = Redraws().Length;

        // New objects, with a locked node in front. Only the empty place now before that card is new.
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock([Locked(), .. Original()])));

        Surfaces(editor)[0].Nodes.ShouldBeEmpty();
        Surfaces(editor)[1..].ShouldBe(before);
        Redraws().Length.ShouldBe(++syncs);

        // One of the equal pair goes.
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock([.. Original()[2..]])));
        Surfaces(editor).Length.ShouldBe(2);
        Surfaces(editor).Distinct().Count().ShouldBe(2);
        Redraws().Length.ShouldBe(syncs);

        // A surface splits into two equal ones, then they merge again.
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("z"), Paragraph("z"))));
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("z"), Locked(), Paragraph("z"))));
        Contents(editor).ShouldBe(["z", "z"]);
        Surfaces(editor).Distinct().Count().ShouldBe(2);
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("z"), Paragraph("z"))));
        Contents(editor).ShouldBe(["z|z"]);
        Faulted.ShouldBeFalse();
    }

    [Fact]
    public async Task ASurfaceThatMoved_StillDeliversIntoItsNewPlace()
    {
        ContentDocument? stored = null;
        var editor = Render(OneBlock(Paragraph("a"), Paragraph("b"), Code(), Paragraph("c")), document =>
        {
            stored = document;
            return Task.CompletedTask;
        });
        var last = Surfaces(editor)[1];
        var (keys, generation) = Redraws()[1];
        editor.Render(parameters => parameters.Add(
            e => e.Document, OneBlock(Paragraph("a"), Locked(), Paragraph("b"), Code(), Paragraph("c"))));

        (await editor.InvokeAsync(() => last.ReceiveEdit(1, 0, 1, Patch(generation, keys, (keys[0], "c, edited"))))).ShouldBeTrue();

        Texts(stored!, 0).ShouldBe(["a", "locked", "b", "code", "c, edited"]);
    }

    [Fact]
    public void AnEmptyBlock_OffersOneSurfaceToTypeIn()
    {
        var editor = Render(ContentDocument.Empty("start"));

        editor.FindComponents<RichTextSurface>().ShouldHaveSingleItem();
        Redraws().ShouldHaveSingleItem().Keys.ShouldBeEmpty();
    }
}
