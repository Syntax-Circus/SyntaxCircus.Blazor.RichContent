using System.Collections.Immutable;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

/// <summary>The mode switch, the errors of Write mode, and what happens when its script cannot do its work.</summary>
public sealed class EditorModeTests : IDisposable
{
    private const string Unconvertible = "surface-unconvertible";

    private readonly BunitContext _context = new();
    private readonly List<ContentDocument> _changes = [];
    private readonly List<RichContentEditorMode> _modes = [];
    private IReadOnlyList<RichContentError> _errors = [];
    private BunitJSModuleInterop _module = default!;

    public EditorModeTests()
    {
        _context.Services.AddRichContent();
        _context.Services.AddLogging();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    // Long unless a test is about retrying, so that no retry runs into a test that counts calls.
    private void RetryAfter(TimeSpan delay) =>
        _context.Services.GetRequiredService<RichContentEditorOptions>().ScriptRetryDelay = delay;

    private void SetUpScript()
    {
        RetryAfter(TimeSpan.FromHours(1));
        _module = _context.JSInterop.SetupModule(RichTextSurface.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        // The code card has a module of its own, which answers the same way.
        var code = _context.JSInterop.SetupModule(CodeCard.ModulePath);
        code.Mode = JSRuntimeMode.Loose;
        for (var generation = 1; generation <= 20; generation++)
        {
            SyncOf(generation).SetResult(generation);
            var drawn = generation;
            code.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == drawn).SetResult(drawn);
        }
    }

    private JSRuntimeInvocationHandler<int?> SyncOf(int generation) =>
        _module.Setup<int?>("sync", invocation => (int)invocation.Arguments[4]! == generation);

    private static RichTextNode Text(string text) => new(Kind.Text, Text: text);

    private static RichTextNode Paragraph(string text) => new(Kind.Paragraph, Children: [Text(text)]);

    private static RichTextNode Code(string text) => new(Kind.CodeBlock, Text: text);

    private static ContentDocument Doc(params ContentBlock[] blocks) => new(ContentDocument.CurrentSchemaVersion, [.. blocks]);

    private static ContentDocument OneBlock(params RichTextNode[] nodes) => Doc(new RichTextBlock("one", [.. nodes]));

    private static ContentDocument Mixed() => Doc(
        new RichTextBlock("one", [Paragraph("first"), Code("code"), Paragraph("after")]),
        new CtaBlock("cta", "Heading", null, "Go", "https://example.com/"));

    private IRenderedComponent<ContentDocumentEditor> Render(
        ContentDocument document, RichContentEditorMode? mode = RichContentEditorMode.Write, bool? showSwitch = null) =>
        _context.Render<ContentDocumentEditor>(parameters =>
        {
            parameters.Add(e => e.Document, document)
                .Add(e => e.DocumentChanged, changed => _changes.Add(changed))
                .Add(e => e.ModeChanged, chosen => _modes.Add(chosen))
                .Add(e => e.ErrorsChanged, errors => _errors = errors);
            if (mode is { } chosenMode)
            {
                parameters.Add(e => e.Mode, chosenMode);
            }

            if (showSwitch is { } show)
            {
                parameters.Add(e => e.ShowModeSwitch, show);
            }
        });

    private static AngleSharp.Dom.IElement Switch(IRenderedComponent<ContentDocumentEditor> editor, string name) =>
        editor.FindAll("[data-rt-mode-switch] button").Single(button => button.TextContent.Trim() == name);

    private static bool IsWriting(IRenderedComponent<ContentDocumentEditor> editor) =>
        editor.Find(".doc-editor").HasAttribute("data-rt-editor");

    private static RichTextSurface[] Surfaces(IRenderedComponent<ContentDocumentEditor> editor) =>
        [.. editor.FindComponents<RichTextSurface>().Select(surface => surface.Instance)];

    // The keys and generation of the latest forced sync that gave the surface nodes with this text.
    private (string[] Keys, int Generation) Drawn(string text)
    {
        var call = _module.Invocations["sync"].Last(invocation => (bool)invocation.Arguments[3]!
            && invocation.Arguments[1] is IReadOnlyList<KeyedNode> nodes
            && nodes.Any(node => node.Node.Children?.Any(child => child.Text == text) == true));
        return ([.. ((IReadOnlyList<KeyedNode>)call.Arguments[1]!).Select(node => node.Key)], (int)call.Arguments[4]!);
    }

    private static string Patch(int generation, string[] order, params (string Key, string Text)[] changed) =>
        JsonSerializer.Serialize(
            new SurfacePatch(
                generation,
                order,
                [.. changed.Select(c => new KeyedNode(c.Key, new SurfaceNode("paragraph", Children: [new SurfaceNode("text", Text: c.Text)])))]),
            JsonSerializerOptions.Web);

    private static long _sequence;

    private static Task<bool> SendAsync(IRenderedComponent<ContentDocumentEditor> editor, ScriptEditorBase part, string payload) =>
        editor.InvokeAsync(() => part.ReceiveEdit(Interlocked.Increment(ref _sequence), 0, 1, payload));

    private string[] Codes() => [.. _errors.Select(error => error.Code)];

    [Fact]
    public void Default_IsBlocks_ExistingMarkupTestsStillPass()
    {
        var editor = Render(Mixed(), mode: null);

        editor.Instance.Mode.ShouldBe(RichContentEditorMode.Blocks);
        editor.Instance.ShowModeSwitch.ShouldBeTrue();
        IsWriting(editor).ShouldBeFalse();
        Switch(editor, "Blocks").GetAttribute("aria-pressed").ShouldBe("true");
        Switch(editor, "Write").GetAttribute("aria-pressed").ShouldBe("false");
        editor.FindAll("[data-rt-surface], [data-rt-toolbar]").ShouldBeEmpty();
        editor.FindAll("section.block").Count.ShouldBe(2);
        editor.FindAll("details.insert-menu").Count.ShouldBeGreaterThan(0);
        _context.JSInterop.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public void ModeSwitch_RaisesModeChanged_NotDocumentChanged()
    {
        SetUpScript();
        var editor = Render(Mixed(), mode: null);

        Switch(editor, "Write").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeTrue());
        Surfaces(editor).Length.ShouldBeGreaterThan(0);
        _modes.ShouldBe([RichContentEditorMode.Write]);

        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        _modes.ShouldBe([RichContentEditorMode.Write, RichContentEditorMode.Blocks]);
        // Clicking the mode already shown does nothing.
        Switch(editor, "Blocks").Click();
        _modes.Count.ShouldBe(2);
        _changes.ShouldBeEmpty();
    }

    [Fact]
    public void ASwitchNotBoundByTheHost_Stays_WhenTheHostRendersAgain_AndAHostChangeWins()
    {
        SetUpScript();
        var editor = Render(Mixed(), mode: RichContentEditorMode.Blocks);
        Switch(editor, "Write").Click();

        editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));
        IsWriting(editor).ShouldBeTrue();

        editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Write));
        IsWriting(editor).ShouldBeTrue();
        editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        // Only the author's own choice is raised: the host knows what it chose.
        _modes.ShouldBe([RichContentEditorMode.Write]);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    [InlineData("hangs")]
    [InlineData("no answer from a surface")]
    public async Task ASwitchWaits_UntilEveryPartHasConfirmedItIsReadOnly(string outcome)
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        // The call that makes a surface read-only, and has it send what it still holds, does not get through.
        var readOnly = _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!);
        switch (outcome)
        {
            case "disconnected":
                readOnly.SetException(new JSDisconnectedException("lost"));
                break;
            case "cancelled":
                readOnly.SetCanceled();
                break;
            case "no answer from a surface":
                readOnly.SetResult(null);
                break;
        }

        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];

        Switch(editor, "Blocks").Click();
        await Task.Delay(300, Xunit.TestContext.Current.CancellationToken);

        IsWriting(editor).ShouldBeTrue();
        _modes.ShouldBeEmpty();
        surface.Disabled.ShouldBeTrue();
        _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!).SetResult(1);

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(10));
        // Raised just after the render that shows the new view.
        SpinWait.SpinUntil(() => _modes.Count > 0, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        _modes.ToArray().ShouldBe([RichContentEditorMode.Blocks]);
    }

    // An extension editor with a surface of its own, which does not pass on that it has been disabled.
    private sealed class CarelessEditor : ExtensionBlockEditorBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<RichTextSurface>(0);
            builder.CloseComponent();
        }
    }

    private sealed class CarelessView : ComponentBase;

    [Fact]
    public void ASurfaceThatIsNeverAskedToBeReadOnly_DoesNotHoldUpASwitch()
    {
        _context.Services.AddSingleton(new ContentBlockRegistry().Add(
            "careless", "Careless", typeof(CarelessView), typeof(CarelessEditor), () => JsonSerializer.SerializeToElement(1)));
        SetUpScript();
        var editor = Render(Doc(new ExtensionBlock("x", "careless", JsonSerializer.SerializeToElement(1))));
        editor.FindComponent<CarelessEditor>().FindComponent<RichTextSurface>();

        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(10));
    }

    private bool[] SyncedDisabled() => [.. _module.Invocations["sync"].Select(call => (bool)call.Arguments[2]!)];

    [Fact]
    public void ASwitchCancelledAfterItsReadOnlyAnswerWasLost_MakesTheSurfaceEditableAgain()
    {
        SetUpScript();
        // The call arrives, and its answer never does.
        _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!);
        var editor = Render(OneBlock(Paragraph("first")));

        Switch(editor, "Blocks").Click();
        SyncedDisabled().Last().ShouldBeTrue();
        Switch(editor, "Write").Click();

        // The browser may well be read-only now, so it must be told otherwise.
        editor.WaitForAssertion(() => SyncedDisabled().Last().ShouldBeFalse(), TimeSpan.FromSeconds(5));
        IsWriting(editor).ShouldBeTrue();
        Surfaces(editor)[0].Disabled.ShouldBeFalse();
    }

    // Answers every synchronization, but only after a while, as over a slow connection.
    private sealed class SlowScript(TimeSpan delay) : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => identifier switch
        {
            "import" => ValueTask.FromResult((TValue)(object)this),
            "sync" => AnswerAsync<TValue>((int)args![4]!),
            _ => ValueTask.FromResult(default(TValue)!),
        };

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async ValueTask<TValue> AnswerAsync<TValue>(int generation)
        {
            await Task.Delay(delay);
            return (TValue)(object)generation;
        }
    }

    [Fact]
    public void AnswersSlowerThanTheTriesAreApart_StillLetASwitchFinish()
    {
        _context.Services.AddSingleton<IJSRuntime>(new SlowScript(TimeSpan.FromMilliseconds(500)));
        // Every answer comes after the next try has been sent.
        RetryAfter(TimeSpan.FromMilliseconds(80));
        var editor = Render(OneBlock(Paragraph("first")));

        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(8));
    }

    [Theory]
    // Who asked for the switch, and who calls it off before it has happened.
    [InlineData("host", "author", true)]
    [InlineData("author", "author", false)]
    [InlineData("host", "host", false)]
    [InlineData("author", "host", false)]
    public async Task ACancelledSwitch_TellsAHostThatHoldsAnotherView(string asked, string cancelled, bool told)
    {
        SetUpScript();
        _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!);
        var editor = Render(OneBlock(Paragraph("first")));

        if (asked == "host")
        {
            editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));
        }
        else
        {
            Switch(editor, "Blocks").Click();
        }

        if (cancelled == "author")
        {
            Switch(editor, "Write").Click();
        }
        else if (asked == "host")
        {
            editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Write));
        }
        else
        {
            // A host that moves its own value away and back again.
            editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));
            editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Write));
        }

        await Task.Delay(300, Xunit.TestContext.Current.CancellationToken);
        IsWriting(editor).ShouldBeTrue();
        _modes.ToArray().ShouldBe(told ? [RichContentEditorMode.Write] : []);
    }

    [Fact]
    public void APartWhoseScriptIsBroken_DoesNotHoldUpASwitch()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        // Drawn once; every call after that throws.
        _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!).SetException(new JSException("broken"));

        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void HiddenSwitch_NotRendered()
    {
        var editor = Render(Mixed(), mode: null, showSwitch: false);

        editor.FindAll("[data-rt-mode-switch]").ShouldBeEmpty();
    }

    [Fact]
    public void ADisabledEditor_StillSwitchesViews_AndItsContentStaysReadOnly()
    {
        SetUpScript();
        var editor = Render(Mixed());
        editor.Render(parameters => parameters.Add(e => e.Disabled, true));

        Switch(editor, "Blocks").HasAttribute("disabled").ShouldBeFalse();
        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        editor.FindAll("input, textarea, select").ShouldAllBe(field => field.HasAttribute("disabled"));
    }

    // bUnit does not let an import be set up to fail.
    private sealed class FailingImport : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException($"{identifier}: the module could not be fetched"));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("attach")]
    public void ModuleImportFails_ShowsNotice_AndBlocks(string step)
    {
        if (step == "import")
        {
            _context.Services.AddSingleton<IJSRuntime>(new FailingImport());
        }
        else
        {
            SetUpScript();
            _module.SetupVoid("attach", _ => true).SetException(new JSException("attach failed"));
        }

        var editor = Render(Mixed());

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        editor.Find(".rt-load-failed").TextContent.ShouldContain("could not be loaded");
        editor.Find(".rt-load-failed").GetAttribute("role").ShouldBe("alert");
        editor.FindAll("[data-rt-surface], [data-rt-toolbar]").ShouldBeEmpty();
        editor.FindAll("section.block textarea").Count.ShouldBeGreaterThan(0);
        _modes.ShouldBe([RichContentEditorMode.Blocks]);
        _changes.ShouldBeEmpty();
        _context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void AfterALoadFailure_WriteCanBeTriedAgain()
    {
        SetUpScript();
        var attach = _module.SetupVoid("attach", _ => true);
        attach.SetException(new JSException("attach failed"));
        var editor = Render(Mixed());
        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        attach.SetVoidResult();

        Switch(editor, "Write").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeTrue());
        editor.FindAll(".rt-load-failed").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    public void AnAttachThatDoesNotArrive_IsShown_AndTriedAgain_WithoutLeavingWriteMode(string outcome)
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        var attach = _module.SetupVoid("attach", _ => true);
        if (outcome == "cancelled")
        {
            attach.SetCanceled();
        }
        else
        {
            attach.SetException(new JSDisconnectedException("gone"));
        }

        var editor = Render(OneBlock(Paragraph("first")));

        editor.WaitForAssertion(() => editor.Find(".rt-surface-stalled").TextContent.ShouldNotBeEmpty(), TimeSpan.FromSeconds(10));
        IsWriting(editor).ShouldBeTrue();
        attach.SetVoidResult();

        editor.WaitForAssertion(
            () => _module.Invocations["sync"].Count(call => (bool)call.Arguments[3]!).ShouldBeGreaterThan(0), TimeSpan.FromSeconds(10));
        editor.WaitForAssertion(() => editor.FindAll(".rt-surface-stalled").ShouldBeEmpty(), TimeSpan.FromSeconds(10));
        IsWriting(editor).ShouldBeTrue();
        _modes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    [InlineData("throws")]
    [InlineData("hangs")]
    [InlineData("not applied")]
    public void ALostRedraw_IsSentAgainByItself_AndShownMeanwhile(string outcome)
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        var editor = Render(OneBlock(Paragraph("first")));
        var second = SyncOf(2);
        switch (outcome)
        {
            case "disconnected":
                second.SetException(new JSDisconnectedException("lost"));
                break;
            case "cancelled":
                second.SetCanceled();
                break;
            case "throws":
                second.SetException(new JSException("lost"));
                break;
            case "not applied":
                second.SetResult(1);
                break;
        }

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("from the host"))));
        editor.WaitForAssertion(() => editor.Find(".rt-surface-stalled"), TimeSpan.FromSeconds(10));
        SyncOf(2).SetResult(2);

        editor.WaitForAssertion(() => editor.FindAll(".rt-surface-stalled").ShouldBeEmpty(), TimeSpan.FromSeconds(10));
        _modes.ShouldBeEmpty();
        _context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task ARetry_NeverRedrawsFromParametersOlderThanWhatWasTyped()
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        // The browser draws, and the answer is lost.
        SyncOf(1).SetCanceled();
        ImmutableArray<RichTextNode>? stored = null;
        // This parent never passes the nodes back, as when rendering is held back.
        var surface = _context.Render<RichTextSurface>(parameters => parameters
            .Add(s => s.Nodes, [Paragraph("first")])
            .Add(s => s.NodesChanged, nodes => stored = nodes));
        var (keys, generation) = Drawn("first");
        (await surface.InvokeAsync(() => surface.Instance.ReceiveEdit(
            Interlocked.Increment(ref _sequence), 0, 1, Patch(generation, keys, (keys[0], "typed"))))).ShouldBeTrue();
        var calls = _module.Invocations["sync"].Count;
        SyncOf(1).SetResult(1);

        surface.WaitForAssertion(
            () => _module.Invocations["sync"].Count.ShouldBeGreaterThan(calls), TimeSpan.FromSeconds(10));

        _module.Invocations["sync"].Where(call => (bool)call.Arguments[3]!).ShouldAllBe(call => (int)call.Arguments[4]! == 1);
        string.Concat(stored!.Value[0].Children.Select(child => child.Text)).ShouldBe("typed");
    }

    [Fact]
    public void TriesCausedByNewParameters_DoNotUseUpTheTimersOwn()
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(200));
        var editor = Render(OneBlock(Paragraph("first")));
        SyncOf(2).SetException(new JSException("cannot draw"));
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("from the host"))));
        // Each of these sends the redraw again, and it fails again.
        for (var i = 0; i < 6; i++)
        {
            editor.Render(parameters => parameters.Add(e => e.Disabled, i % 2 == 0));
        }

        editor.WaitForAssertion(() => editor.Find(".rt-surface-stalled"), TimeSpan.FromSeconds(10));
        SyncOf(2).SetResult(2);

        // Nothing else happens.
        editor.WaitForAssertion(() => editor.FindAll(".rt-surface-stalled").ShouldBeEmpty(), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("cancelled")]
    [InlineData("hangs")]
    public void AToolbarAttachThatDoesNotArrive_IsShown_AndTriedAgain(string outcome)
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        var attach = _module.SetupVoid("attachToolbar", _ => true);
        if (outcome == "cancelled")
        {
            attach.SetCanceled();
        }
        else if (outcome == "disconnected")
        {
            attach.SetException(new JSDisconnectedException("gone"));
        }

        var editor = Render(OneBlock(Paragraph("first")));

        editor.WaitForAssertion(() => editor.Find(".rt-toolbar-stalled"), TimeSpan.FromSeconds(10));
        IsWriting(editor).ShouldBeTrue();
        _module.SetupVoid("attachToolbar", _ => true).SetVoidResult();

        editor.WaitForAssertion(() => editor.FindAll(".rt-toolbar-stalled").ShouldBeEmpty(), TimeSpan.FromSeconds(10));
        _modes.ShouldBeEmpty();
        _context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void AToolbarScriptThatThrows_FallsBackToBlocks()
    {
        SetUpScript();
        _module.SetupVoid("attachToolbar", _ => true).SetException(new JSException("broken"));

        var editor = Render(OneBlock(Paragraph("first")));

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(10));
        editor.Find(".rt-load-failed");
    }

    [Fact]
    public async Task ALoadFailure_DoesNotTakeAwayWhatCouldNotBeSaved()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        await SendAsync(editor, surface, "not a patch");
        // Only now does a code card appear, and its script cannot be used.
        _context.JSInterop.SetupModule(CodeCard.ModulePath).SetupVoid("attach", _ => true).SetException(new JSException("broken"));

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("first"), Code("added"))));

        editor.WaitForAssertion(() => editor.Find(".rt-surface-failed"), TimeSpan.FromSeconds(10));
        editor.WaitForAssertion(() => editor.Find(".rt-switch-refused"), TimeSpan.FromSeconds(10));
        IsWriting(editor).ShouldBeTrue();
        Surfaces(editor).ShouldContain(surface);
        Codes().ShouldContain(Unconvertible);
    }

    [Fact]
    public void AFirstDrawThatIsCancelled_AndThenThrows_FallsBackToBlocks()
    {
        SetUpScript();
        RetryAfter(TimeSpan.FromMilliseconds(30));
        SyncOf(1).SetCanceled();
        var editor = Render(OneBlock(Paragraph("first")));
        SyncOf(1).SetException(new JSException("cannot draw"));

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse(), TimeSpan.FromSeconds(10));
        editor.Find(".rt-load-failed");
    }

    [Fact]
    public async Task ARejectedEdit_IsReportedThroughErrorsChanged_BeforeItIsAnswered_UntilALaterEditApplies()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");
        _errors.ShouldBeEmpty();

        await editor.InvokeAsync(() =>
        {
            var answer = surface.ReceiveEdit(Interlocked.Increment(ref _sequence), 0, 1, "not a patch");

            // A click on Save is handled next, so the host must know already.
            Codes().ShouldBe([Unconvertible]);
            return answer;
        });

        var error = _errors.ShouldHaveSingleItem();
        error.BlockId.ShouldBe("one");
        error.BlockIndex.ShouldBe(0);
        error.Message.ShouldNotBeNullOrWhiteSpace();
        // Beside the surface, and once.
        editor.FindAll("[role=alert]").Count(alert => alert.TextContent.Contains(error.Message)).ShouldBe(1);
        editor.Find(".rt-writing .rt-surface-error").TextContent.ShouldContain(error.Message);

        (await SendAsync(editor, surface, Patch(generation, keys, (keys[0], "fine")))).ShouldBeTrue();

        _errors.ShouldBeEmpty();
        editor.FindAll(".rt-surface-error").ShouldBeEmpty();
    }

    [Fact]
    public async Task WhileARejectionStands_APatchThatDoesNotCarryEverything_CannotEndIt()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");
        await SendAsync(editor, surface, "not a patch");
        var partial = JsonSerializer.Serialize(new SurfacePatch(generation, keys, [], Partial: true), JsonSerializerOptions.Web);

        (await SendAsync(editor, surface, partial)).ShouldBeFalse("the browser must keep it");

        Codes().ShouldBe([Unconvertible]);
        (await SendAsync(editor, surface, Patch(generation, keys, (keys[0], "everything")))).ShouldBeTrue();
        _errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a gap")]
    [InlineData("too large")]
    [InlineData("too deep")]
    public async Task WhateverCannotBeTakenIn_IsReported(string what)
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");

        var answer = await editor.InvokeAsync(() => what switch
        {
            "a gap" => surface.ReceiveEdit(Interlocked.Increment(ref _sequence), 1, 2, "x"),
            "too large" => surface.ReceiveEdit(Interlocked.Increment(ref _sequence), 0, 1, new string('x', 2 * 1024 * 1024 + 1)),
            _ => surface.ReceiveEdit(Interlocked.Increment(ref _sequence), 0, 1, Deep(generation, keys[0])),
        });

        answer.ShouldBeFalse();
        Codes().ShouldBe([Unconvertible]);
        _changes.ShouldBeEmpty();
    }

    // A quote holding lists nested past the depth a surface can write.
    private static string Deep(int generation, string key)
    {
        var node = new SurfaceNode("text", Text: "deep");
        for (var level = 0; level < 5; level++)
        {
            node = new SurfaceNode("list", Children: [new SurfaceNode("listItem", Children: level == 0 ? [node] : [new SurfaceNode("text", Text: "x"), node])]);
        }

        return JsonSerializer.Serialize(new SurfacePatch(generation, [key], [new KeyedNode(key, node)]), JsonSerializerOptions.Web);
    }

    [Fact]
    public async Task ALargerTextLimit_RaisesWhatOneEditMayHold()
    {
        SetUpScript();
        var editor = _context.Render<ContentDocumentEditor>(parameters => parameters
            .Add(e => e.Document, OneBlock(Paragraph("first")))
            .Add(e => e.Mode, RichContentEditorMode.Write)
            .Add(e => e.Limits, new RichContentLimits(TextBytes: 2 * 1024 * 1024))
            .Add(e => e.DocumentChanged, changed => _changes.Add(changed)));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");

        var payload = Patch(generation, keys, (keys[0], new string('x', 2 * 1024 * 1024)));

        (await SendAsync(editor, surface, payload)).ShouldBeTrue();
        _changes.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASwitchIsRefused_WhileSomethingShownCouldNotBeSaved(bool byHost)
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");
        await SendAsync(editor, surface, "not a patch");

        if (byHost)
        {
            editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));
        }
        else
        {
            Switch(editor, "Blocks").Click();
        }

        editor.WaitForAssertion(() => editor.Find(".rt-switch-refused"));
        IsWriting(editor).ShouldBeTrue();
        Surfaces(editor)[0].ShouldBeSameAs(surface);
        surface.Disabled.ShouldBeFalse("it must be editable again, to be put right");
        Codes().ShouldBe([Unconvertible]);
        // A host that had already moved on is told what is shown.
        SpinWait.SpinUntil(() => !byHost || _modes.Count > 0, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        _modes.ToArray().ShouldBe(byHost ? [RichContentEditorMode.Write] : []);
        // Known to be refused, so nothing was made read-only on the way.
        _module.Invocations["sync"].ShouldAllBe(call => !(bool)call.Arguments[2]!);
        Switch(editor, "Blocks").HasAttribute("data-rt-rearrange").ShouldBeFalse();

        await SendAsync(editor, surface, Patch(generation, keys, (keys[0], "fine")));
        editor.FindAll(".rt-switch-refused").ShouldBeEmpty();
        Switch(editor, "Blocks").Click();

        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeFalse());
        _errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARejectionThatArrivesWhileASwitchIsOnItsWay_RefusesIt()
    {
        SetUpScript();
        var readOnly = _module.Setup<int?>("sync", invocation => (bool)invocation.Arguments[2]!);
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        Switch(editor, "Blocks").Click();
        surface.Disabled.ShouldBeTrue();

        // Sent before the browser made the surface read-only, and so ahead of its answer.
        await SendAsync(editor, surface, "not a patch");
        readOnly.SetResult(1);

        editor.WaitForAssertion(() => editor.Find(".rt-switch-refused"), TimeSpan.FromSeconds(10));
        IsWriting(editor).ShouldBeTrue();
        _modes.ShouldBeEmpty();
        surface.Disabled.ShouldBeFalse();
    }

    [Fact]
    public async Task AReplacementThatRedrawsTheSurface_EndsItsError()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        await SendAsync(editor, Surfaces(editor)[0], "not a patch");

        // The same value in new objects leaves the surface, what it shows and its error alone.
        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("first"))));
        Codes().ShouldBe([Unconvertible]);

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("from the host"))));

        editor.WaitForAssertion(() => _errors.ShouldBeEmpty());
        editor.FindAll(".rt-surface-error").ShouldBeEmpty();
    }

    [Fact]
    public async Task ALostRedraw_LeavesTheErrorStanding()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        await SendAsync(editor, Surfaces(editor)[0], "not a patch");
        SyncOf(2).SetCanceled();

        editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("from the host"))));

        Codes().ShouldBe([Unconvertible]);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("cell")]
    public async Task ACardWhoseContentCouldNotBeSaved_ReportsIt_AsksBeforeItGoes_AndItsErrorGoesWithIt(string kind)
    {
        SetUpScript();
        // Nothing stored in it, so only what is on screen is at stake.
        var card = kind == "code"
            ? Code("")
            : new RichTextNode(Kind.Table, Children:
                [new RichTextNode(Kind.TableRow, Children: [new RichTextNode(Kind.TableCell, Children: [Text("")])])]);
        var editor = Render(OneBlock(Paragraph("first"), card));
        _errors = [];
        ScriptEditorBase part = kind == "code"
            ? editor.FindComponent<CodeCard>().Instance
            : editor.FindComponents<RichTextSurface>().Select(s => s.Instance).Single(s => s.Profile == RichTextSurfaceProfile.Inline);

        (await SendAsync(editor, part, "not a patch")).ShouldBeFalse();
        Codes().ShouldContain(Unconvertible);
        editor.Find("[data-rt-card] .rt-surface-error");

        editor.Find("[data-rt-card] .rt-card-header [aria-label=Remove]").Click();
        editor.Find("[data-rt-confirm]");
        _changes.ShouldBeEmpty();
        editor.FindAll("[data-rt-confirm] button")[0].Click();

        editor.WaitForAssertion(() => Codes().ShouldNotContain(Unconvertible));
        editor.FindAll("[data-rt-card]").ShouldBeEmpty();
    }

    [Fact]
    public async Task ARearrangementThatWouldDropUnsavedText_IsRefused_WithTheReason()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first"), Code("code"), Paragraph("after")));
        var after = Surfaces(editor)[1];
        var (keys, generation) = Drawn("after");
        await SendAsync(editor, after, "not a patch");

        // Removing the card would join the two surfaces, redrawing both.
        editor.Find("[data-rt-card=code] .rt-card-header [aria-label=Remove]").Click();
        editor.FindAll("[data-rt-confirm] button")[0].Click();

        _changes.ShouldBeEmpty();
        editor.Find("[data-rt-card=code]");
        editor.Find(".rt-refused").TextContent.ShouldContain("could not be saved");
        editor.Find(".rt-refused").GetAttribute("role").ShouldBe("alert");

        await SendAsync(editor, after, Patch(generation, keys, (keys[0], "after")));
        editor.Find("[data-rt-card=code] .rt-card-header [aria-label=Move\\ up]").Click();

        _changes.Count.ShouldBe(1);
        editor.FindAll(".rt-refused").ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInsertThatIsRefused_SaysWhy()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        await SendAsync(editor, surface, "not a patch");

        await editor.InvokeAsync(() => surface.InsertRequested(0, null, 0));

        _changes.ShouldBeEmpty();
        editor.Find(".rt-refused").TextContent.ShouldContain("could not be saved");
    }

    [Fact]
    public async Task AnEmptyPlaceWithUnsavedText_SurvivesABlockBeingRemovedBesideIt()
    {
        SetUpScript();
        // A place stands before the first callout, between them, and after the second.
        var editor = Render(Doc(
            new CalloutBlock("a", CalloutTone.Info, null, [Paragraph("a")]),
            new CalloutBlock("b", CalloutTone.Info, null, [Paragraph("b")]),
            new CalloutBlock("c", CalloutTone.Info, null, [Paragraph("c")])));
        var between = editor.FindComponents<RichTextSurface>()
            .Select(s => s.Instance)
            .Where(s => s.Nodes.IsEmpty)
            .ElementAt(1);
        // The surfaces of the callouts' own bodies are empty places of their own; take a document-level one.
        var places = editor.FindAll("section[data-rt-place]");
        places.Count.ShouldBe(4);
        var placeSurface = editor.FindComponents<WritingBlock>()
            .Where(block => block.Instance.Nodes.IsEmpty)
            .ElementAt(1)
            .FindComponent<RichTextSurface>()
            .Instance;
        await SendAsync(editor, placeSurface, "not a patch");

        // Its neighbour "a" goes, which would otherwise make this a different place.
        editor.Find("[data-block-id=a] > header [aria-label=Remove]").Click();
        editor.FindAll("[data-rt-confirm] button")[0].Click();
        editor.Render(parameters => parameters.Add(e => e.Document, _changes[^1]));

        editor.WaitForAssertion(() => editor.FindAll("[data-block-id=a]").ShouldBeEmpty());
        editor.FindComponents<RichTextSurface>().Select(s => s.Instance).ShouldContain(placeSurface);
        placeSurface.HasRejectedEdit.ShouldBeTrue();
        Codes().ShouldContain(Unconvertible);
    }

    [Theory]
    [InlineData("switch", "a later edit")]
    [InlineData("switch", "a replacement")]
    [InlineData("block", "a later edit")]
    [InlineData("insert", "a later edit")]
    [InlineData("insert", "the stored content again")]
    public async Task ARefusal_GoesWithWhatCausedIt_AndDoesNotComeBack(string refused, string putRight)
    {
        SetUpScript();
        // Equal ids, so that rearranging blocks would redraw them, which is what the editor refuses.
        var editor = Render(refused == "block"
            ? Doc(new RichTextBlock("one", [Paragraph("first")]), new RichTextBlock("one", [Paragraph("second")]))
            : OneBlock(Paragraph("first")));
        var surface = Surfaces(editor)[0];
        var (keys, generation) = Drawn("first");
        await SendAsync(editor, surface, "not a patch");
        var alert = refused == "switch" ? ".rt-switch-refused" : ".rt-refused";
        switch (refused)
        {
            case "switch":
                Switch(editor, "Blocks").Click();
                break;
            case "block":
                editor.FindAll("section.block > header button").First(button => button.TextContent == "Move down").Click();
                break;
            default:
                await editor.InvokeAsync(() => surface.InsertRequested(0, null, 0));
                break;
        }

        editor.WaitForAssertion(() => editor.Find(alert));

        if (putRight == "a replacement")
        {
            editor.Render(parameters => parameters.Add(e => e.Document, OneBlock(Paragraph("from the host"))));
            generation = 2;
        }
        else
        {
            var text = putRight == "a later edit" ? "fine" : "first";
            (await SendAsync(editor, surface, Patch(generation, keys, (keys[0], text)))).ShouldBeTrue();
        }

        editor.WaitForAssertion(() => _errors.Select(error => error.Code).ShouldNotContain(Unconvertible));
        editor.FindAll(alert).ShouldBeEmpty();

        // Something else cannot be saved, with nothing asked for since.
        await SendAsync(editor, surface, "not a patch either " + generation);

        Codes().ShouldContain(Unconvertible);
        editor.FindAll(alert).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusal_GoesWithTheCardWhoseRemovalEndedItsCause()
    {
        SetUpScript();
        var editor = Render(OneBlock(Paragraph("first"), Code("code")));
        var surface = Surfaces(editor)[0];
        await SendAsync(editor, editor.FindComponent<CodeCard>().Instance, "not a patch");
        Switch(editor, "Blocks").Click();
        editor.WaitForAssertion(() => editor.Find(".rt-switch-refused"));

        // The error goes with the card, in the middle of a render.
        editor.Find("[data-rt-card] .rt-card-header [aria-label=Remove]").Click();
        editor.FindAll("[data-rt-confirm] button")[0].Click();
        editor.WaitForAssertion(() => Codes().ShouldNotContain(Unconvertible));
        await SendAsync(editor, surface, "not a patch");

        editor.FindAll(".rt-switch-refused").ShouldBeEmpty();
    }

    [Theory]
    // The block menu, and Insert in a writing surface of a block that comes later.
    [InlineData("menu")]
    [InlineData("surface")]
    public async Task AnInsertThatWouldRedrawUnsavedText_BecauseBlocksShareAnId_IsRefused(string from)
    {
        SetUpScript();
        var editor = Render(Doc(
            new RichTextBlock("same", [Paragraph("first")]),
            new RichTextBlock("same", [Paragraph("second"), Code("code")])));
        var surfaces = Surfaces(editor);
        var unsaved = surfaces[0];
        await SendAsync(editor, unsaved, "not a patch");

        if (from == "menu")
        {
            editor.Find(".insert-menu[data-position='0'] button:not([disabled])").Click();
        }
        else
        {
            // After the paragraph and before the card, which splits that block. The choice is the call to action,
            // which follows the choices of nodes, the gallery and the callout.
            var callToAction = InsertChoice.ForNodes(RichContentLimits.Default.Depth).Count + 2;
            var (keys, _) = Drawn("second");
            await editor.InvokeAsync(() => surfaces[1].InsertRequested(callToAction, keys[0], 0));
        }

        _changes.ShouldBeEmpty();
        editor.Find(".rt-refused").TextContent.ShouldContain("could not be saved");
        Surfaces(editor)[0].ShouldBeSameAs(unsaved);
        Codes().ShouldContain(Unconvertible);
        // Nothing of the block asked from went anywhere.
        editor.FindAll("[data-rt-card=code]").Count.ShouldBe(1);
    }

    [Fact]
    public async Task ATextBlockWithUnsavedText_AsksBeforeItIsRemoved()
    {
        SetUpScript();
        var editor = Render(Mixed());
        await SendAsync(editor, Surfaces(editor)[0], "not a patch");

        editor.Find("[data-block-id=one] > header button:last-child").Click();

        editor.Find("[data-block-id=one] [data-rt-confirm]");
        _changes.ShouldBeEmpty();
        editor.FindAll("[data-block-id=one] [data-rt-confirm] button")[0].Click();
        _changes.Count.ShouldBe(1);
        editor.Render(parameters => parameters.Add(e => e.Document, _changes[^1]));
        editor.WaitForAssertion(() => Codes().ShouldNotContain(Unconvertible));
    }

    [Fact]
    public void ValidationErrors_ShowBesideTheirOwnSurfaceOrCard()
    {
        SetUpScript();
        var unsafeLink = new RichTextNode(Kind.Paragraph, Children:
            [new RichTextNode(Kind.Link, Url: "javascript:alert(1)", Children: [Text("bad")])]);
        var noAlt = new RichTextNode(Kind.Image, Media: new MediaRef("x"), Alt: "");
        var editor = Render(Doc(
            new RichTextBlock("one", [Paragraph("fine"), Code("ok"), unsafeLink, noAlt]),
            new CalloutBlock("callout", CalloutTone.Info, null, [Code(new string('x', 40 * 1024))])));

        var parts = editor.Find("[data-block-id=one] .rt-writing").Children
            .Select(child => (Kind: child.GetAttribute("data-rt-card") ?? child.ClassName ?? child.TagName, child.TextContent))
            .ToArray();
        var beside = parts.Select(part => part.Kind).ToList();
        // surface, code card, surface with the link, its errors, image card, its errors, closing place.
        var lists = editor.FindAll("[data-block-id=one] .rt-part-errors");
        lists.Count.ShouldBe(2, string.Join(", ", beside));
        lists[0].TextContent.ShouldContain("The link URL is not allowed.");
        lists[0].PreviousElementSibling!.GetAttribute("data-rt-surface").ShouldNotBeNull();
        lists[1].TextContent.ShouldContain("alt text");
        lists[1].PreviousElementSibling!.GetAttribute("data-rt-card").ShouldBe("image");
        // Not listed again under the block.
        editor.FindAll("[data-block-id=one] > .errors").ShouldBeEmpty();
        // Inside a callout too.
        editor.Find("[data-block-id=callout] .rt-part-errors").TextContent.ShouldContain("code block");
        editor.FindAll("[data-block-id=callout] > .errors").ShouldBeEmpty();
        Codes().ShouldContain("unsafe-url");
    }

    [Fact]
    public void ABlocksEditTheHostRefused_DoesNotComeBackInWriteMode()
    {
        SetUpScript();
        // A host that keeps its own document.
        var editor = Render(Doc(new CtaBlock("cta", "Kept", null, "Go", "https://example.com/")), mode: RichContentEditorMode.Blocks);
        editor.Find("[data-block-id=cta] input").Input("Refused");
        _changes.Count.ShouldBe(1);

        Switch(editor, "Write").Click();
        editor.WaitForAssertion(() => IsWriting(editor).ShouldBeTrue());
        editor.Find("[data-block-id=cta] input").GetAttribute("value").ShouldBe("Kept");
        editor.FindAll("[data-block-id=cta] input")[1].Input("Go on");

        var block = (CtaBlock)_changes[^1].Blocks.Single(b => b.Id == "cta");
        block.Heading.ShouldBe("Kept");
        block.Label.ShouldBe("Go on");
    }
}
