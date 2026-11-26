using System.Text.Json.Nodes;
using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// Switching modes, a host that replaces the document, content that cannot be saved, a disabled editor, a lost
/// connection and removal: none of them may lose what was typed or break the circuit. Everything here runs on
/// SignalR's default settings.
/// </summary>
public class ResilienceTests(SampleHost host) : CardTest(host)
{
    private const string Typed = "LOSTTEXT";

    private ILocator ModeButton(string name) =>
        Page.Locator("[data-rt-mode-switch]").GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private ILocator FirstParagraph => Surface("prose").Locator(":scope > p").First;

    private async Task ShouldBeWritingAsync()
    {
        await Expect(ModeButton("Write")).ToHaveAttributeAsync("aria-pressed", "true");
        await WaitForSurfacesAsync(Page);
        await Expect(Page.Locator("textarea[data-rt-code]:not([data-rt-ready])")).ToHaveCountAsync(0);
    }

    private async Task ShouldShowBlocksAsync()
    {
        await Expect(ModeButton("Blocks")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Page.Locator("[data-rt-surface], [data-rt-toolbar]")).ToHaveCountAsync(0);
    }

    // Puts text at the end of the element's last text, as typing would, and raises the input event, in this task.
    // Optionally clicks something in the same task, so that nothing can come between.
    private static async Task TypeByScriptAsync(ILocator surface, string text, string? thenClick = null) =>
        await surface.EvaluateAsync(
            """
            (element, [text, thenClick]) => {
                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                const first = walker.nextNode();
                first.appendData(text);
                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
                if (thenClick) document.querySelector(thenClick).click();
            }
            """,
            new[] { text, thenClick });

    private const string BlocksButton = "[data-rt-mode-switch] button:last-child";

    private async Task PasteAsync(int paragraphs, int characters) =>
        await Page.EvaluateAsync(
            """
            ([paragraphs, characters]) => {
                const text = Array.from({ length: paragraphs }, (_, i) => `p${i} `.padEnd(characters, 'x')).join('\n\n');
                const data = { getData: type => (type === 'text/plain' ? text : ''), types: ['text/plain'] };
                const event = new Event('paste', { bubbles: true, cancelable: true });
                Object.defineProperty(event, 'clipboardData', { value: data });
                document.activeElement.dispatchEvent(event);
            }
            """,
            new[] { paragraphs, characters });

    private ILocator Reported(string code) => Page.Locator("#errors li").Filter(new() { HasTextString = code });

    private async Task UndoUntilAsync(ILocator condition, int count)
    {
        for (var attempt = 0; attempt < 5 && await condition.CountAsync() != count; attempt++)
        {
            await Page.Keyboard.PressAsync("ControlOrMeta+z");
            // What the undo changed has to get to the host and back before it shows whether that was enough.
            for (var waited = 0; waited < 40 && await condition.CountAsync() != count; waited++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        await Expect(condition).ToHaveCountAsync(count, new() { Timeout = 30_000 });
    }

    // ---- Switching ----

    [Theory]
    [InlineData("everything", false)]
    [InlineData("everything", true)]
    [InlineData("unknown", false)]
    public async Task SwitchingModesBothWays_RaisesNoChange(string fixture, bool strictCsp)
    {
        await OpenFixtureAsync(fixture, "blocks", strictCsp);
        await ShouldShowBlocksAsync();

        for (var round = 0; round < 2; round++)
        {
            await ModeButton("Write").ClickAsync();
            await ShouldBeWritingAsync();
            await ModeButton("Blocks").ClickAsync();
            await ShouldShowBlocksAsync();
        }

        (await SaveAsync(Page)).ShouldBe(Original(fixture));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Theory]
    // What was typed has been sent; its message was lost without a word; or it never settled and nothing says so yet.
    [InlineData("pass")]
    [InlineData("drop")]
    [InlineData("hang")]
    public async Task TextTypedRightBeforeASwitch_IsKept(string fate)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await TapAsync(fate);
        await PlaceCaretAsync(FirstParagraph);

        await TypeByScriptAsync(Surface("prose"), Typed, BlocksButton);

        await ShouldShowBlocksAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldContain(Typed);
    }

    [Fact]
    public async Task ACompositionInProgress_SurvivesASwitch()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await PlaceCaretAsync(FirstParagraph);

        // Composed text is on screen and not yet sent when the switch is clicked, and the composition never ends.
        await Surface("prose").EvaluateAsync(
            """
            (surface, [text, button]) => {
                surface.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
                document.createTreeWalker(surface, NodeFilter.SHOW_TEXT).nextNode().appendData(text);
                surface.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true }));
                document.querySelector(button).click();
            }
            """,
            new[] { Typed, BlocksButton });

        await ShouldShowBlocksAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldContain(Typed);
    }

    [Theory]
    // Each is a field or element that the switch replaces.
    [InlineData("surface")]
    [InlineData("code")]
    [InlineData("field")]
    [InlineData("blocks-field")]
    public async Task NothingTypedWhileASwitchIsNotYetDrawn_IsLost(string where)
    {
        await Page.AddInitScriptAsync(HeldRenderTests.HoldScript);
        var fromBlocks = where == "blocks-field";
        await OpenFixtureAsync("everything", fromBlocks ? "blocks" : "write");
        if (!fromBlocks)
        {
            await ShouldBeWritingAsync();
        }

        var target = await (where switch
        {
            "surface" => Surface("prose"),
            "code" => Page.Locator("[data-block-id=prose] textarea[data-rt-code]").First,
            "field" => Page.Locator("[data-block-id=prose] [data-rt-card=image]").First.GetByLabel("Caption"),
            _ => Page.Locator("[data-block-id=prose] textarea").First,
        }).ElementHandleAsync();
        await Page.EvaluateAsync("() => window.hold()");

        // The host's switch: unlike the editor's own, its click makes nothing read-only. By script, past the fixture.
        await Page.Locator(fromBlocks ? "#mode-write" : "#mode-blocks").EvaluateAsync("button => button.click()");
        // Everything the server has sent is held back, so the old view is still on show.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        await target.EvaluateAsync(
            """
            element => {
                element.focus();
                if (element.setSelectionRange) {
                    element.setSelectionRange(element.value.length, element.value.length);
                    return;
                }

                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                const text = walker.nextNode();
                getSelection().collapse(text, text.length);
            }
            """);
        await Page.Keyboard.TypeAsync(Typed);
        (await target.EvaluateAsync<bool>("(element, typed) => (element.value ?? element.textContent).includes(typed)", Typed))
            .ShouldBeTrue("nothing has told the page to stop taking it");
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await Page.EvaluateAsync("() => window.resume()");

        if (fromBlocks)
        {
            await ShouldBeWritingAsync();
        }
        else
        {
            await ShouldShowBlocksAsync();
        }

        (await SaveAsync(Page)).ShouldContain(Typed, Case.Sensitive);
    }

    [Fact]
    public async Task TextTypedWhileDisconnected_SurvivesASwitch()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await PlaceCaretAsync(FirstParagraph);
        var outage = await Watch.DropConnectionAsync(Page);
        await TypeByScriptAsync(Surface("prose"), Typed);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();

        // At once, before any retry need have come round.
        await ModeButton("Blocks").ClickAsync();

        await ShouldShowBlocksAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldContain(Typed);
    }

    [Theory]
    // However long after reconnecting the surface would next have tried by itself.
    [InlineData(1)]
    [InlineData(8)]
    public async Task TextTypedWhileDisconnected_SurvivesASwitchTheHostMakesMeanwhile(int secondsOfOutage)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        // The host switches two seconds from now, by which time the connection is down.
        await Page.Locator("#mode-blocks-later").ClickAsync();
        await SaveAsync(Page);
        var outage = await Watch.DropConnectionAsync(Page);
        await TypeByScriptAsync(Surface("prose"), Typed);
        await Task.Delay(TimeSpan.FromSeconds(2 + secondsOfOutage), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();

        await ShouldShowBlocksAsync();

        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldContain(Typed);
    }

    [Fact]
    public async Task ASwitchIsRefused_WhileTextCouldNotBeSaved_AndWorksOnceItCan()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await Page.Keyboard.TypeAsync("kept");
        await PasteAsync(4200, 8);
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        var shown = await Start.TextContentAsync();

        await ModeButton("Blocks").ClickAsync();

        await Expect(Page.Locator(".rt-switch-refused")).ToBeVisibleAsync();
        await Expect(ModeButton("Write")).ToHaveAttributeAsync("aria-pressed", "true");
        (await Start.TextContentAsync()).ShouldBe(shown);
        await Expect(Start).ToHaveAttributeAsync("contenteditable", "true");
        await Expect(Start).Not.ToHaveAttributeAsync("inert", new System.Text.RegularExpressions.Regex(".*"));
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1);

        // The host's own switch is refused too.
        await Page.Locator("#mode-blocks").ClickAsync();
        // Two round trips: the first has the read-only view drawn, and the second its refusal.
        await SaveAsync(Page);
        await SaveAsync(Page);
        await Expect(Page.Locator(".rt-switch-refused")).ToBeVisibleAsync();
        await Expect(Start).ToHaveAttributeAsync("contenteditable", "true");
        await Expect(Start).Not.ToHaveAttributeAsync("inert", new System.Text.RegularExpressions.Regex(".*"));
        (await Start.TextContentAsync()).ShouldBe(shown);

        await Start.FocusAsync();
        await UndoUntilAsync(Reported("surface-unconvertible"), 0);
        await ModeButton("Blocks").ClickAsync();

        await ShouldShowBlocksAsync();
        TextOf(Nodes(await SaveAsync(Page), "start")[0]).ShouldBe("kept");
    }

    [Fact]
    public async Task WithoutItsToolbarWired_NothingInAWritingEditorCanBeTyped()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        var area = await CodeAsync("prose");

        // As in the moment after the view has changed to Write, before the toolbar's script has it.
        await Page.EvaluateAsync(
            """
            async () => {
                const module = await import('./_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
                module.detachToolbar(document.querySelector('.doc-editor').richContentFreeze.key);
            }
            """);

        await Expect(Page.Locator(EditableSurfaces)).ToHaveCountAsync(0);
        await Expect(area).Not.ToBeEditableAsync();
    }

    // While window.loseAnswers is set, the page's answers to .NET's calls never leave it, as in a connection that is
    // failing. Everything else it sends still goes.
    private const string LoseAnswersScript = """
        (() => {
            const Watched = window.WebSocket;
            window.WebSocket = class extends Watched {
                send(data) {
                    const text = typeof data === 'string' ? data : new TextDecoder().decode(data);
                    if (!window.loseAnswers || !text.includes('EndInvokeJSFromDotNet')) {
                        super.send(data);
                    }
                }
            };
        })();
        """;

    [Fact]
    public async Task ARedrawWhoseAnswerWasLost_ThenAHostSwitch_KeepsWhatWasTypedInBetween()
    {
        await Page.AddInitScriptAsync(LoseAnswersScript);
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await Page.EvaluateAsync("() => { window.loseAnswers = true; }");
        await Page.Locator("#replace-other").ClickAsync();
        // The browser has drawn it; .NET cannot know.
        await Expect(FirstParagraph).ToHaveTextAsync(Fixtures.ReplacementText);
        // And what is typed now does not get through either.
        await TapAsync("hang");
        await PlaceCaretAsync(FirstParagraph);
        await Page.Keyboard.InsertTextAsync(Typed);

        // The connection is good again, and the host switches before anything has been tried again.
        await Page.EvaluateAsync("() => { window.loseAnswers = false; document.getElementById('mode-blocks').click(); }");

        await ShouldShowBlocksAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldBe(Fixtures.ReplacementText + Typed);
    }

    [Theory]
    [InlineData("surface")]
    [InlineData("code")]
    public async Task ADisablingSyncThatCarriesARedraw_StillSendsWhatIsOutstanding(string kind)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        var target = kind == "code" ? await CodeAsync("prose") : Surface("prose");

        // The script alone: an edit that has not got through, then a disabling sync that sends again the redraw the
        // browser already has.
        var sent = await target.EvaluateAsync<int>(
            """
            async (element, kind) => {
                const part = element.richContentSurface ?? element.richContentCode;
                let count = 0;
                part.transport.invoke = () => (++count === 1 ? new Promise(() => {}) : Promise.resolve(true));
                if (kind === 'code') {
                    element.value += ' unsent';
                } else {
                    element.querySelector('p').append(' unsent');
                }

                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
                const module = await import(
                    `./_content/SyntaxCircus.Blazor.RichContent/surface/${kind === 'code' ? 'code' : 'surface'}.js`);
                const before = count;
                module.sync(element, null, true, true, part.generation);
                return count - before;
            }
            """,
            kind);

        sent.ShouldBeGreaterThan(0);
    }

    // ---- Undo and the freeze ----

    [Theory]
    // None of these redraws the surface typed in. The last two make the whole editor read-only for a while.
    [InlineData("remove-asked")]
    [InlineData("card-move")]
    [InlineData("block-move")]
    [InlineData("switch-refused-late")]
    public async Task NativeUndo_OfEarlierTyping_SurvivesTheEditorBeingReadOnlyForAWhile(string what)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        var surface = Surface("more-prose");
        await PlaceCaretAsync(surface.Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync("UNDOME");
        await WaitUntilAcknowledgedAsync();
        await surface.EvaluateAsync("e => { e.kept = true; }");

        var card = Card("prose", "code").First;
        switch (what)
        {
            case "remove-asked":
                await card.Locator(".rt-card-header").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
                await card.GetByRole(AriaRole.Button, new() { Name = "No", Exact = true }).ClickAsync();
                break;
            case "card-move":
                await card.GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true }).ClickAsync();
                await WaitForChangesAsync(1);
                break;
            case "block-move":
                await Page.Locator("[data-block-id=callout-warning] > header")
                    .GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true }).ClickAsync();
                await WaitForChangesAsync(1);
                break;
            default:
                // Too much for one edit, with the switch clicked in the same task: it is refused only once under way.
                await Surface("prose").EvaluateAsync(
                    """
                    (surface, button) => {
                        surface.querySelector(':scope > p').textContent = 'x'.repeat(2_200_000);
                        surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                        document.querySelector(button).click();
                    }
                    """,
                    BlocksButton);
                await Expect(Page.Locator(".rt-switch-refused")).ToBeVisibleAsync(new() { Timeout = 60_000 });
                break;
        }

        await Expect(Page.Locator(EditableSurfaces)).ToHaveCountAsync(
            await Page.Locator("[data-rt-surface]").CountAsync(), new() { Timeout = 30_000 });
        (await surface.EvaluateAsync<bool>("e => e.kept === true")).ShouldBeTrue("the surface was not drawn again");
        await surface.FocusAsync();
        for (var attempt = 0; attempt < 6 && (await surface.TextContentAsync())!.Contains("UNDOME", StringComparison.Ordinal); attempt++)
        {
            await Page.Keyboard.PressAsync("ControlOrMeta+z");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        (await surface.TextContentAsync()).ShouldBe("A second rich text block, next to the first.");
    }

    [Fact]
    public async Task NothingGetsIntoAFrozenEditor_ThoughTheFocusWasInIt()
    {
        await Page.AddInitScriptAsync(HeldRenderTests.HoldScript);
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        var surface = Surface("more-prose");
        var paragraph = surface.Locator(":scope > p").First;
        await PlaceCaretAsync(paragraph);
        await Page.Keyboard.TypeAsync("typed ");
        await WaitUntilAcknowledgedAsync();
        var shown = await surface.InnerHTMLAsync();
        await Page.EvaluateAsync("() => window.hold()");

        // By script, so that the focus is still in the surface when the click freezes it.
        await Card("prose", "code").First.GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true })
            .EvaluateAsync("button => button.click()");

        async Task ShouldBeAsBeforeAsync(string after) => (await surface.InnerHTMLAsync()).ShouldBe(shown, after);

        await Page.Keyboard.TypeAsync(Typed);
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.PressAsync("Enter");
        await ShouldBeAsBeforeAsync("keys");
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("Delete");
        await ShouldBeAsBeforeAsync("select all and delete");
        await Page.Keyboard.PressAsync("ControlOrMeta+z");
        await Page.Keyboard.PressAsync("ControlOrMeta+y");
        await Page.Keyboard.PressAsync("ControlOrMeta+Shift+z");
        await ShouldBeAsBeforeAsync("undo and redo");
        await surface.EvaluateAsync("() => { document.execCommand('insertText', false, 'EXEC'); document.execCommand('bold'); }");
        await ShouldBeAsBeforeAsync("execCommand");
        (await surface.EvaluateAsync<bool>(
            """
            async surface => {
                const module = await import('./_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
                return module.command(surface, 'bold') || module.command(surface, 'bullets');
            }
            """)).ShouldBeFalse();
        await ShouldBeAsBeforeAsync("toolbar commands");
        await PasteAsync(1, 40);
        await ShouldBeAsBeforeAsync("a paste");
        await surface.EvaluateAsync("surface => surface.focus()");
        await Page.Keyboard.TypeAsync("FOCUSED");
        await ShouldBeAsBeforeAsync("focus by script");
        await paragraph.ClickAsync(new() { Force = true });
        await Page.Keyboard.TypeAsync("CLICKED");
        await ShouldBeAsBeforeAsync("a click");
        if (SampleHost.BrowserName == "chromium")
        {
            var session = await Page.Context.NewCDPSessionAsync(Page);
            await session.SendAsync("Input.imeSetComposition", new() { ["text"] = "KOMP", ["selectionStart"] = 4, ["selectionEnd"] = 4 });
            await session.SendAsync("Input.insertText", new() { ["text"] = "COMPOSED" });
            await ShouldBeAsBeforeAsync("an input method");
        }

        await Page.EvaluateAsync("() => window.resume()");
        await Expect(Page.Locator("[data-rt-surface]:not([contenteditable=true]), [data-rt-surface][inert]")).ToHaveCountAsync(0);
        await PlaceCaretAsync(paragraph);
        await Page.Keyboard.TypeAsync("after");
        TextOf(Nodes(await SaveAsync(Page), "more-prose")[0]).ShouldEndWith("typed after");
    }

    [Fact]
    public async Task AThaw_LeavesInertAsItWas()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        // Set by something else, on one surface.
        await Surface("more-prose").EvaluateAsync("surface => { surface.inert = true; }");

        await Card("prose", "code").First.GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true }).ClickAsync();
        await WaitForChangesAsync(1);

        await Expect(Page.Locator("[data-rt-surface][inert]")).ToHaveCountAsync(1);
        (await Surface("more-prose").EvaluateAsync<bool>("surface => surface.inert")).ShouldBeTrue();
        await WaitForSurfacesAsync(Page);
    }

    [Fact]
    public async Task ACompositionJustBeforeAFreeze_IsDelivered()
    {
        Assert.SkipUnless(SampleHost.BrowserName == "chromium", "Only Chromium's protocol can drive an input method.");
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await PlaceCaretAsync(Surface("more-prose").Locator(":scope > p").First);
        var session = await Page.Context.NewCDPSessionAsync(Page);
        await session.SendAsync("Input.imeSetComposition", new() { ["text"] = Typed, ["selectionStart"] = 8, ["selectionEnd"] = 8 });

        await Card("prose", "code").First.GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true })
            .EvaluateAsync("button => button.click()");
        await WaitForChangesAsync(1);

        TextOf(Nodes(await SaveAsync(Page), "more-prose")[0]).ShouldEndWith(Typed);
    }

    // ---- Replacement by the host ----

    [Fact]
    public async Task EqualReplacement_KeepsCaretUndoAndText()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await PlaceCaretAsync(FirstParagraph);
        await Page.Keyboard.TypeAsync("abc");
        await FirstParagraph.EvaluateAsync("paragraph => { paragraph.kept = true; }");
        // The next edit is still on its way when the copy is made.
        await TapAsync("hold");
        await Page.Keyboard.TypeAsync("d");

        // By script, so that the focus and the caret stay where they are.
        await Page.EvaluateAsync("() => document.getElementById('replace-same').click()");
        await SaveAsync(Page);
        await FirstParagraph.EvaluateAsync("paragraph => paragraph.closest('[data-rt-surface]').focus()");
        await Page.Keyboard.TypeAsync("ef");
        await Page.EvaluateAsync("() => window.release()");
        await WaitUntilAcknowledgedAsync();

        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldEndWith("a link.abcdef");
        (await FirstParagraph.EvaluateAsync<bool>("paragraph => paragraph.kept === true")).ShouldBeTrue();
        await FirstParagraph.EvaluateAsync("paragraph => paragraph.closest('[data-rt-surface]').focus()");
        await UndoUntilAsync(FirstParagraph.Filter(new() { HasTextString = "abc" }), 0);
        await WaitUntilAcknowledgedAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldEndWith("a link.");
    }

    [Fact]
    public async Task DifferentReplacement_Redraws_OnlyChangedSurface()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await Page.EvaluateAsync(
            "() => document.querySelectorAll('[data-rt-surface] > *').forEach(node => { node.kept = true; })");
        var before = await Page.EvaluateAsync<int>("() => document.querySelectorAll('[data-rt-surface] > *').length");

        await Page.Locator("#replace-other").ClickAsync();

        await Expect(FirstParagraph).ToHaveTextAsync(Fixtures.ReplacementText);
        // Only the surface that holds the changed node was drawn again.
        var redrawn = await Page.EvaluateAsync<int[]>(
            """
            () => [...document.querySelectorAll('[data-rt-surface]')]
                .map((surface, index) => [...surface.children].some(node => !node.kept) ? index : -1)
                .filter(index => index >= 0)
            """);
        var first = await Surface("prose").EvaluateAsync<int>(
            "surface => [...document.querySelectorAll('[data-rt-surface]')].indexOf(surface)");
        redrawn.ShouldBe([first]);
        before.ShouldBeGreaterThan(10);
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    // ---- Disabled ----

    [Theory]
    [InlineData("write")]
    [InlineData("blocks")]
    public async Task Disabled_NothingEditable(string mode)
    {
        await OpenFixtureAsync("everything", mode);
        if (mode == "write")
        {
            await ShouldBeWritingAsync();
        }

        await Page.Locator("#toggle-disabled").ClickAsync();

        await Expect(Page.Locator(".doc-editor details button").First).ToBeDisabledAsync();
        await Expect(Page.Locator("[data-rt-surface][contenteditable=true]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("textarea[data-rt-code]:not([readonly])")).ToHaveCountAsync(0);
        var enabled = () => Page.Locator(".doc-editor").EvaluateAsync<string[]>(
            """
            editor => [...editor.querySelectorAll('button, input, select, textarea:not([data-rt-code])')]
                .filter(control => !control.disabled && !control.closest('[data-rt-mode-switch]'))
                .map(control => control.outerHTML)
            """);
        (await enabled()).ShouldBeEmpty();
        if (mode == "write")
        {
            var shown = await Surface("prose").TextContentAsync();
            await FirstParagraph.ClickAsync(new() { Force = true });
            await Page.Keyboard.TypeAsync(Typed);
            (await Surface("prose").TextContentAsync()).ShouldBe(shown);
        }

        // The view can still be changed, and what it then shows is read-only too.
        await ModeButton(mode == "write" ? "Blocks" : "Write").ClickAsync();
        if (mode == "write")
        {
            await ShouldShowBlocksAsync();
        }
        else
        {
            await ShouldBeWritingAsync();
        }

        await Expect(Page.Locator("[data-rt-surface][contenteditable=true]")).ToHaveCountAsync(0);
        (await enabled()).ShouldBeEmpty();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await ChangeCountAsync(Page)).ShouldBe(0);

        await Page.Locator("#toggle-disabled").ClickAsync();
        await ModeButton("Write").ClickAsync();
        await ShouldBeWritingAsync();
        await Expect(Page.Locator("[data-rt-surface][contenteditable=false]")).ToHaveCountAsync(0);
        await PlaceCaretAsync(FirstParagraph);
        await Page.Keyboard.TypeAsync("!");
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldEndWith("!");
    }

    // ---- The connection ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_SameCircuit_DeliversTypedText(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        await ShouldBeWritingAsync();
        var code = await CodeAsync("prose");

        // The watch fails the test on an uncaught error, and on a circuit other than this one afterwards.
        var outage = await Watch.DropConnectionAsync(Page);
        await TypeByScriptAsync(Surface("prose"), "OFFLINE");
        await code.EvaluateAsync(
            "area => { area.value += 'OFFLINECODE'; area.dispatchEvent(new InputEvent('input', { bubbles: true })); }");
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();
        await WaitUntilAcknowledgedAsync();
        await Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('[data-rt-code]')].every(e => e.richContentCode.transport.unacknowledged === 0)");

        var saved = await SaveAsync(Page);
        saved.ShouldContain("OFFLINECODE");
        TextOf(Nodes(saved, "prose")[0]).ShouldContain("OFFLINE");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        await Expect(Page.Locator(".rt-surface-stalled, .rt-load-failed")).ToHaveCountAsync(0);
        await ShouldBeWritingAsync();
    }

    // ---- Large and unconvertible content ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste200Kb_IntoAnEmptyDocument_IsSaved(bool strictCsp)
    {
        await OpenFixtureAsync("empty", "write", strictCsp);
        await Start.ClickAsync();

        await PasteAsync(200, 1024);
        var saved = await SaveAsync(Page);

        var nodes = Nodes(saved, "start");
        nodes.Count.ShouldBe(200);
        nodes.Sum(node => TextOf(node).Length).ShouldBe(200 * 1024);
        TextOf(nodes[199]).ShouldStartWith("p199 ");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Theory]
    // More nodes than the rules allow, which still converts; and more than a surface can send at all.
    [InlineData(2200, "too-many-nodes")]
    [InlineData(4200, "surface-unconvertible")]
    public async Task OverNodeLimitPaste_KeepsTextOnScreen_ReportsError_CircuitAlive(int paragraphs, string code) =>
        await ShouldBeReportedAndKeptAsync(paragraphs, 8, code);

    [Theory]
    // More text than the rules allow, which still converts; and more than one edit may carry.
    [InlineData(300, 1000, "text-too-large")]
    [InlineData(600, 4000, "surface-unconvertible")]
    public async Task OverTextLimitPaste_SameResult(int paragraphs, int characters, string code) =>
        await ShouldBeReportedAndKeptAsync(paragraphs, characters, code);

    private async Task ShouldBeReportedAndKeptAsync(int paragraphs, int characters, string code)
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();

        await PasteAsync(paragraphs, characters);

        await Expect(Reported(code).First).ToBeAttachedAsync(new() { Timeout = 60_000 });
        await Expect(Start.Locator(":scope > p")).ToHaveCountAsync(paragraphs);
        var unconvertible = code == "surface-unconvertible";
        await Expect(Page.Locator(".rt-surface-error")).ToHaveCountAsync(unconvertible ? 1 : 0);
        // The circuit is alive. What could not be converted is not in the document, and the error says so.
        var saved = await SaveAsync(Page);
        if (unconvertible)
        {
            saved.ShouldBe(Original("empty"));
            (await ErrorCodesAsync(Page)).ShouldContain(code);
        }
        else
        {
            Nodes(saved, "start").Count.ShouldBe(paragraphs);
        }

        await Start.FocusAsync();
        await UndoUntilAsync(Reported(code), 0);
        await WaitUntilAcknowledgedAsync();
        await Start.FocusAsync();
        await Page.Keyboard.TypeAsync("after");
        TextOf(Nodes(await SaveAsync(Page), "start")[0]).ShouldBe("after");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TypingElsewhereBeforeARejectionIsHeardOf_DoesNotClearItsError()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();

        // In one task: too much for one edit in one paragraph, then a small change to another, which does not carry
        // the first and is sent before anything has been heard of it.
        await Surface("prose").EvaluateAsync(
            """
            surface => {
                const [first, second] = surface.querySelectorAll(':scope > p, :scope > h2');
                const input = () => surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                first.textContent = 'x'.repeat(2_200_000);
                input();
                second.textContent += ' small';
                input();
            }
            """);

        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1, new() { Timeout = 60_000 });
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var saved = await SaveAsync(Page);

        // Nothing of the surface can be taken while part of it cannot, and the host is told so.
        saved.ShouldBe(Original("everything"));
        (await ErrorCodesAsync(Page)).ShouldContain("surface-unconvertible");
    }

    [Fact]
    public async Task ErrorsShowBesideTheirSurface()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await PlaceCaretAsync(Surface("more-prose").Locator(":scope > p").First);
        await PasteAsync(1, 300_000);

        var block = Page.Locator("[data-block-id=more-prose]");
        await Expect(block.Locator(".rt-writing .rt-part-errors")).ToContainTextAsync("Total text may be at most");
        await Expect(Page.Locator(".rt-part-errors")).ToHaveCountAsync(1);
        await Expect(block.Locator(":scope > .errors")).ToHaveCountAsync(0);
        await Expect(Reported("text-too-large").First).ToBeAttachedAsync();

        // A card's error is beside the card.
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await InsertAsync("Image");
        await Expect(Page.Locator("[data-block-id=prose] [data-rt-card=image] + .rt-part-errors"))
            .ToContainTextAsync("Image requires a media reference.");
    }

    [Fact]
    public async Task ACardRemovalThatWouldLoseUnsavedText_SaysWhyItDidNotHappen()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        // The surface after the first code card; removing that card would join it to the one before.
        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > p").First);
        await PasteAsync(4200, 8);
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        var card = Card("prose", "code").First;

        await card.Locator(".rt-card-header").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await card.GetByRole(AriaRole.Button, new() { Name = "Yes", Exact = true }).ClickAsync();

        await Expect(Page.Locator(".rt-refused")).ToContainTextAsync("could not be saved");
        await Expect(Page.Locator("[data-block-id=prose] [data-rt-card=code]")).ToHaveCountAsync(2);
        await Expect(Surface("prose", 1).Locator(":scope > p")).Not.ToHaveCountAsync(1);
        (await ChangeCountAsync(Page)).ShouldBe(0);

        // Insert says why too.
        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > p").First);
        await InsertAsync("Code block");
        await Expect(Page.Locator(".rt-refused")).ToContainTextAsync("could not be saved");
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Theory]
    // A block splits the text block, so the cards after the cursor would be drawn again in a block of their own.
    [InlineData("code")]
    [InlineData("table")]
    public async Task ABlockInsertedAboveACardWithUnsavedText_IsRefused_AndTheTextStays(string kind)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        await CodeAsync("prose");
        var field = kind == "code"
            ? Card("prose", "code").First.Locator("textarea[data-rt-code]")
            : Card("prose", "table").Locator("[data-rt-surface]").First;
        // More than one edit may carry, so it can be nowhere but on screen.
        await field.EvaluateAsync(
            """
            element => {
                const text = 'x'.repeat(2_200_000);
                if (element.matches('textarea')) {
                    element.value = text;
                } else {
                    element.textContent = text;
                }

                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1, new() { Timeout = 60_000 });
        const string Length = "element => (element.value ?? element.textContent).length";

        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await InsertAsync("Call to action");

        await Expect(Page.Locator("[data-block-id=prose] .rt-refused")).ToContainTextAsync("could not be saved");
        (await ChangeCountAsync(Page)).ShouldBe(0);
        (await field.EvaluateAsync<int>(Length)).ShouldBe(2_200_000);
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-block-kind=cta]")).ToHaveCountAsync(1);

        // What redraws no card is still done, and the card keeps what it shows.
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await InsertAsync("Image");
        await Expect(Page.Locator("[data-block-id=prose] [data-rt-card=image]")).ToHaveCountAsync(3);
        (await field.EvaluateAsync<int>(Length)).ShouldBe(2_200_000);
        await Expect(Reported("surface-unconvertible")).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-block-id=prose] .rt-refused")).ToHaveCountAsync(0);
    }

    // ---- Removal ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_RemovingBlock_NoConsoleErrors(bool whileAnEditIsOutstanding)
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeWritingAsync();
        if (whileAnEditIsOutstanding)
        {
            await TapAsync("hang");
        }

        await PlaceCaretAsync(Surface("more-prose").Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync("x");
        await Page.Locator("[data-block-id=more-prose] > header")
            .GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await Expect(Page.Locator("[data-block-id=more-prose]")).ToHaveCountAsync(0);

        // A block with surfaces, a code card and a table in it.
        var callout = Page.Locator("[data-block-id=callout-tip] > header");
        await callout.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await callout.GetByRole(AriaRole.Button, new() { Name = "Yes", Exact = true }).ClickAsync();
        await Expect(Page.Locator("[data-block-id=callout-tip]")).ToHaveCountAsync(0);

        // Long enough for a retry of what was outstanding, which must find nothing to send to.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var saved = await SaveAsync(Page);
        saved.ShouldNotContain("more-prose");
        saved.ShouldNotContain("callout-tip");
        // The test then leaves the page, which disposes the circuit and everything still on it.
    }
}

/// <summary>
/// When a script of the writing view cannot be loaded or fails to draw. The browser and the server log those as
/// errors on purpose, so these tests have a server of their own and take the failures they cause.
/// </summary>
public class LoadFailureTests(SampleHost host) : CardTest(host)
{
    private readonly SampleServer _server = SampleServer.Start();

    protected override IReadOnlyList<SampleServer> Servers => [_server];

    public override async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _server.DisposeAsync();
        }
    }

    private async Task GotoAsync(string mode) => await Page.GotoAsync(new Uri(_server.Address, $"fixture/everything?mode={mode}").AbsoluteUri);

    [Theory]
    // The module the editor imports, the code card's own, and two that the first imports.
    [InlineData("surface.js")]
    [InlineData("code.js")]
    [InlineData("freeze.js")]
    [InlineData("dom.js")]
    public async Task AScriptThatCannotBeLoaded_ShowsANotice_AndTheBlockEditor(string script)
    {
        var name = script[..^3];
        await Page.RouteAsync(
            new System.Text.RegularExpressions.Regex($@"/_content/SyntaxCircus\.Blazor\.RichContent/surface/{name}(\.[a-z0-9]+)?\.js"),
            route => route.AbortAsync());

        await GotoAsync("write");

        await Expect(Page.Locator(".rt-load-failed")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("[data-rt-surface], [data-rt-toolbar], textarea[data-rt-code]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-rt-mode-switch] button[aria-pressed=true]")).ToHaveTextAsync("Blocks");
        // The block editor works.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Heading").FillAsync("Edited without the script.");
        var saved = await SaveAsync(Page);
        saved.ShouldContain("Edited without the script.");
        (await ChangeCountAsync(Page)).ShouldBe(1);

        // What was caused on purpose: the failed request, and what the server logged about it.
        var page = await Watch.TakeFailuresAsync();
        page.ShouldAllBe(failure => failure.Kind == PageFailureKind.ConsoleError);
        _server.Log.TakeFailures().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TextNotYetDelivered_WhenAScriptCannotBeLoaded_IsSavedByTheFallback()
    {
        // The code card's script is asked for only when the first code card appears. The request is kept waiting.
        var asked = new TaskCompletionSource<IRoute>();
        await Page.RouteAsync(
            new System.Text.RegularExpressions.Regex(@"/_content/SyntaxCircus\.Blazor\.RichContent/surface/code(\.[a-z0-9]+)?\.js"),
            route =>
            {
                asked.TrySetResult(route);
                return Task.CompletedTask;
            });
        await FixturePage.OpenAsync(Page, _server.Address, "empty", "write");
        await Surface("start").ClickAsync();
        await InsertAsync("Code block");
        var request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var surface = Page.Locator("[data-rt-surface]").First;
        await Expect(Page.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
        await Expect(surface).ToHaveAttributeAsync("contenteditable", "true");
        // What is typed now does not get through.
        await TapAsync("hang");
        await surface.FocusAsync();
        await Page.Keyboard.InsertTextAsync("TYPEDBEFOREFAILURE");

        await request.AbortAsync();

        await Expect(Page.Locator(".rt-load-failed")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("[data-rt-surface]")).ToHaveCountAsync(0);
        (await SaveAsync(Page)).ShouldContain("TYPEDBEFOREFAILURE", Case.Sensitive);
        (await Watch.TakeFailuresAsync()).ShouldAllBe(failure => failure.Kind == PageFailureKind.ConsoleError);
        _server.Log.TakeFailures().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ALaterRedrawThatGoesOnFailing_SaysThatRetriesHaveStopped_UntilTriedAgain()
    {
        await FixturePage.OpenAsync(Page, _server.Address, "everything", "write");
        var surface = Surface("prose");
        await surface.EvaluateAsync(
            """
            element => {
                element.replaceChildren = () => { throw new Error('deliberate lasting draw failure'); };
                window.heal = () => delete element.replaceChildren;
            }
            """);

        await Page.Locator("#replace-other").ClickAsync();

        var notice = Page.Locator("[data-block-id=prose] .rt-surface-stalled").First;
        await Expect(notice).ToContainTextAsync("Trying again.", new() { Timeout = 30_000 });
        // At the delays the package uses, the tries run out after about half a minute.
        await Expect(notice).ToContainTextAsync("Automatic retries have stopped.", new() { Timeout = 60_000 });
        await Expect(notice).Not.ToContainTextAsync("Trying again.");
        await Expect(surface).ToHaveAttributeAsync("contenteditable", "false");

        // Healed, and nothing happens until the author asks.
        await Page.EvaluateAsync("() => window.heal()");
        await Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Expect(surface).ToHaveAttributeAsync("contenteditable", "false");
        await notice.GetByRole(AriaRole.Button, new() { Name = "Try again now" }).ClickAsync();

        await Expect(surface).ToHaveAttributeAsync("contenteditable", "true", new() { Timeout = 30_000 });
        await Expect(Page.Locator(".rt-surface-stalled")).ToHaveCountAsync(0);
        await PlaceCaretAsync(surface.Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync(" Kept.");
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldBe(Fixtures.ReplacementText + " Kept.");
        _server.Log.TakeFailures();
    }

    [Fact]
    public async Task ALaterRedrawThatFails_IsShown_AndPutRightByItself()
    {
        await FixturePage.OpenAsync(Page, _server.Address, "everything", "write");
        var surface = Surface("prose");
        await surface.EvaluateAsync(
            """
            element => {
                element.replaceChildren = () => { throw new Error('deliberate later draw failure'); };
                window.heal = () => delete element.replaceChildren;
            }
            """);

        await Page.Locator("#replace-other").ClickAsync();

        var notice = Page.Locator("[data-block-id=prose] .rt-surface-stalled").First;
        await Expect(notice).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(surface).ToHaveAttributeAsync("contenteditable", "false");
        // Nothing else happens: no edit and no change from the host.
        await Page.EvaluateAsync("() => window.heal()");

        await Expect(surface).ToHaveAttributeAsync("contenteditable", "true", new() { Timeout = 30_000 });
        await Expect(Page.Locator(".rt-surface-stalled")).ToHaveCountAsync(0);
        await Expect(surface.Locator(":scope > p").First).ToHaveTextAsync(Fixtures.ReplacementText);
        await PlaceCaretAsync(surface.Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync(" Kept.");
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldBe(Fixtures.ReplacementText + " Kept.");
        _server.Log.TakeFailures();
    }
}
