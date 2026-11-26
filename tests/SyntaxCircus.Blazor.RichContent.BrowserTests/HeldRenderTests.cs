using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// What the author can do between a click that rearranges the editor and the arrival of its result. Everything the
/// server sends, render batches included, is held back, so that the page goes on showing what was there before for as
/// long as the test likes. Nothing typed then may be lost: it must be impossible to type, or be kept.
/// </summary>
public class HeldRenderTests(SampleHost host) : CardTest(host)
{
    private const string Typed = "LOSTTEXT";

    // Added after the watch's own wrapper, so around it. Blazor sets onmessage; messages are handed to it in order,
    // or queued while held.
    internal const string HoldScript = """
        (() => {
            const Watched = window.WebSocket;
            const queue = [];
            let held = false;
            window.hold = () => { held = true; };
            window.resume = () => {
                held = false;
                while (queue.length > 0) queue.shift()();
            };
            window.WebSocket = class extends Watched {
                constructor(...args) {
                    super(...args);
                    let handler = null;
                    this.addEventListener('message', event => {
                        const deliver = () => handler?.call(this, event);
                        if (held || queue.length > 0) queue.push(deliver);
                        else deliver();
                    });
                    Object.defineProperty(this, 'onmessage', { get: () => handler, set: value => { handler = value; } });
                }
            };
        })();
        """;

    // Types where the author last could, whether or not the element takes it, and says whether it did.
    private async Task<bool> TryTypingAsync(IElementHandle target)
    {
        await target.EvaluateAsync("""
            element => {
                element.focus();
                if (element.tagName === 'TEXTAREA' || element.tagName === 'INPUT') {
                    element.setSelectionRange(element.value.length, element.value.length);
                    return;
                }

                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                let last = null;
                for (let text = walker.nextNode(); text; text = walker.nextNode()) last = text;
                const range = document.createRange();
                if (last) range.setStart(last, last.length);
                else range.selectNodeContents(element.firstElementChild ?? element);
                range.collapse(true);
                getSelection().removeAllRanges();
                getSelection().addRange(range);
            }
            """);
        // Letters only: a space or Enter would press a button that still had the focus.
        await Page.Keyboard.TypeAsync(Typed);
        return await target.EvaluateAsync<bool>(
            "(element, typed) => (element.value ?? element.textContent).includes(typed)", Typed);
    }

    private async Task OpenHeldAsync()
    {
        await Page.AddInitScriptAsync(HoldScript);
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        await Expect(Page.Locator("textarea[data-rt-code]:not([data-rt-ready])")).ToHaveCountAsync(0);
    }

    private ILocator ProseSurface(int index) => Surface("prose", index);

    private ILocator PlaceAfter(string blockId) =>
        Page.Locator($"[data-block-id='{blockId}'] ~ section[data-rt-place]").First.Locator("[data-rt-surface]");

    private static ILocator Button(ILocator within, string name) => within.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).First;

    private ILocator CardAt(string blockId, string kind, int index = 0) => Card(blockId, kind).Nth(index);

    private ILocator Header(string blockId) => Page.Locator($"[data-block-id='{blockId}'] > header");

    // What the action needs done before the render is held, what it then does, and what it rewrites or removes.
    private async Task<(Func<Task> Act, ILocator Target)> PrepareAsync(string action)
    {
        var code = CardAt("prose", "code");
        var yes = Button(Page.Locator("#fixture"), "Yes");
        switch (action)
        {
            case "move":
                return (() => Button(code, "Move up").ClickAsync(), ProseSurface(0));
            case "move-enter":
                return (() => Button(code, "Move down").PressAsync("Enter"), ProseSurface(1));
            case "two-moves":
                return (async () =>
                {
                    await Button(code, "Move up").ClickAsync();
                    await Button(code, "Move up").ClickAsync();
                }, ProseSurface(0));
            case "remove-yes":
                await Button(code, "Remove").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.ClickAsync(), ProseSurface(1));
            case "remove-space":
                await Button(code, "Remove").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.PressAsync("Space"), ProseSurface(0));
            case "remove-code-card":
                await Button(code, "Remove").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.ClickAsync(), code.Locator("textarea"));
            case "remove-image-card":
                await Button(CardAt("prose", "image"), "Remove").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.ClickAsync(), CardAt("prose", "image").GetByLabel("Alt text"));
            case "table-row":
                var table = CardAt("prose", "table");
                await Button(table, "Remove last row").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.ClickAsync(), table.Locator("tr").Last.Locator("[data-rt-surface]").First);
            case "insert-code":
            case "insert-block":
                await PlaceCaretAsync(ProseSurface(0).Locator(":scope > h2"), 3);
                await Expect(Tool("Insert")).ToBeEnabledAsync();
                await Tool("Insert").ClickAsync();
                var choice = Page.GetByRole(AriaRole.Group, new() { Name = "Insert", Exact = true })
                    .GetByRole(AriaRole.Button, new() { Name = action == "insert-code" ? "Code block" : "Callout", Exact = true });
                await Expect(choice).ToBeVisibleAsync();
                return (() => choice.ClickAsync(), ProseSurface(0));
            case "gallery-image":
                // The fields stay and are given another image's values: what was typed would go to that image.
                var gallery = Page.Locator("[data-block-id='gallery']");
                return (() => Button(gallery.Locator(".child").First, "Move down").ClickAsync(), gallery.GetByLabel("Alt text").First);
            case "block-move":
                // Moves a callout away from the empty place after it.
                return (() => Button(Header("callout-warning"), "Move up").ClickAsync(), PlaceAfter("callout-tip"));
            case "block-remove":
                await Button(Header("callout-tip"), "Remove").ClickAsync();
                await Expect(yes).ToBeVisibleAsync();
                return (() => yes.ClickAsync(), Page.Locator("[data-block-id='callout-tip'] [data-rt-surface]").First);
            default:
                // "block-menu": a block added beside an empty place takes that place away.
                var menu = Page.Locator("[data-block-id='callout-tip'] + details.insert-menu");
                await menu.Locator("summary").ClickAsync();
                var add = menu.GetByRole(AriaRole.Button, new() { Name = "Text", Exact = true });
                await Expect(add).ToBeVisibleAsync();
                return (() => add.ClickAsync(), PlaceAfter("callout-tip"));
        }
    }

    [Theory]
    [InlineData("move")]
    [InlineData("move-enter")]
    [InlineData("two-moves")]
    [InlineData("remove-yes")]
    [InlineData("remove-space")]
    [InlineData("remove-code-card")]
    [InlineData("remove-image-card")]
    [InlineData("table-row")]
    [InlineData("insert-code")]
    [InlineData("insert-block")]
    [InlineData("block-move")]
    [InlineData("block-remove")]
    [InlineData("block-menu")]
    [InlineData("gallery-image")]
    public async Task NothingTypedWhileARearrangementIsNotYetDrawn_IsLost(string action)
    {
        await OpenHeldAsync();
        var (act, target) = await PrepareAsync(action);
        // The element as it is now: the render may put another in its place.
        var old = await target.ElementHandleAsync();
        var changes = await ChangeCountAsync(Page);

        await Page.EvaluateAsync("() => window.hold()");
        await act();
        var typed = await TryTypingAsync(old);
        // Time for anything typed to be sent and answered, and for the page to ask whether it may go on.
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        typed |= await TryTypingAsync(old);
        (await ChangeCountAsync(Page)).ShouldBe(changes, "the render is held");
        await Page.EvaluateAsync("() => window.resume()");

        await ShouldBeEditableAgainAsync();
        var saved = await SaveAsync(Page);
        (await ChangeCountAsync(Page)).ShouldBeGreaterThan(changes, "the action took effect");
        // The editor is read-only from the click, so nothing can be typed at all. Text that could be would have to be
        // kept, and kept where it was typed.
        typed.ShouldBeFalse(saved.Contains(Typed, StringComparison.Ordinal) ? "the text could be typed" : "typed text was lost");
    }

    private async Task ShouldBeEditableAgainAsync()
    {
        await Expect(Page.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
        await Expect(Page.Locator(ReadOnlySurfaces)).ToHaveCountAsync(0);
        await Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('[data-rt-editor] textarea, [data-rt-editor] input')].every(e => !e.readOnly)");
    }

    [Fact]
    public async Task WhileTheRenderIsHeld_TheEditorStaysReadOnly()
    {
        await OpenHeldAsync();
        await Page.EvaluateAsync("() => window.hold()");

        await Button(CardAt("prose", "code"), "Move up").ClickAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        (await Page.Locator(EditableSurfaces).CountAsync()).ShouldBe(0);
        (await Page.Locator("textarea[data-rt-code]").EvaluateAllAsync<bool>("areas => areas.every(a => a.readOnly)")).ShouldBeTrue();
        (await Page.Locator("[data-rt-card=image] input[type=text]").First.EvaluateAsync<bool>("e => e.readOnly")).ShouldBeTrue();
        await Page.EvaluateAsync("() => window.resume()");
        await ShouldBeEditableAgainAsync();
    }

    [Theory]
    // Asking to remove only opens the question. A removal that .NET refuses changes nothing.
    [InlineData("question")]
    [InlineData("refused")]
    public async Task AClickThatRearrangesNothing_LeavesEverythingEditable(string what)
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        if (what == "refused")
        {
            // Nested too deeply to be stored, so .NET will not redraw this surface, which removing the card would.
            await Surface("prose", 1).EvaluateAsync("""
                surface => {
                    let list = surface.appendChild(document.createElement('ul'));
                    for (let level = 0; level < 4; level++) {
                        const item = list.appendChild(document.createElement('li'));
                        item.append('level ' + level);
                        if (level < 3) list = item.appendChild(document.createElement('ul'));
                    }
                    surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                }
                """);
        }

        var card = CardAt("prose", "code");
        await Button(card, "Remove").ClickAsync();
        if (what == "refused")
        {
            await Button(card, "Yes").ClickAsync();
        }

        await ShouldBeEditableAgainAsync();
        await Expect(Card("prose", "code")).ToHaveCountAsync(2);
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await Page.Keyboard.TypeAsync("!");
        TextOf(Nodes(await SaveAsync(Page), "prose")[1]).ShouldBe("A heading!");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReleaseThatIsLost_OrNeverAnswered_IsAskedForAgain(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        await CodeAsync("prose");
        await Page.EvaluateAsync("""
            () => {
                const state = document.querySelector('[data-rt-editor]').richContentFreeze;
                const real = state.ask;
                const plan = ['drop', 'hang', 'drop'];
                window.asked = 0;
                state.ask = number => {
                    window.asked++;
                    switch (plan.shift()) {
                        case 'drop': return Promise.reject(new Error('forced failure'));
                        case 'hang': return new Promise(() => {});
                        default: return real(number);
                    }
                };
            }
            """);

        await Button(CardAt("prose", "code"), "Move up").ClickAsync();

        await ShouldBeEditableAgainAsync();
        (await Page.EvaluateAsync<int>("() => window.asked")).ShouldBe(4);
    }

    [Fact]
    public async Task InsertChosenWhileDisconnected_LeavesEverythingEditable()
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"), 3);
        await Expect(Tool("Insert")).ToBeEnabledAsync();
        await Tool("Insert").ClickAsync();
        var choice = Page.GetByRole(AriaRole.Group, new() { Name = "Insert", Exact = true })
            .GetByRole(AriaRole.Button, new() { Name = "Code block", Exact = true });
        await Expect(choice).ToBeVisibleAsync();

        var outage = await Watch.DropConnectionAsync(Page);
        // Blazor's reconnect dialog covers the page, so the click is made by script.
        await choice.EvaluateAsync("button => button.click()");

        (await Page.Locator(ReadOnlySurfaces).CountAsync()).ShouldBe(0);
        await outage.RestoreAsync();
        await ShouldBeEditableAgainAsync();
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await Page.Keyboard.TypeAsync("!");
        TextOf(Nodes(await SaveAsync(Page), "prose")[1]).ShouldBe("A heading!");
    }

    [Fact]
    public async Task AFreezeThatTheConnectionDroppedUnder_EndsAfterReconnecting()
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        await Page.EvaluateAsync("""
            () => {
                const state = document.querySelector('[data-rt-editor]').richContentFreeze;
                const real = state.ask;
                window.answer = () => { state.ask = real; };
                state.ask = () => new Promise(() => {});
            }
            """);
        await Button(CardAt("prose", "code"), "Move up").ClickAsync();
        await WaitForChangesAsync(1);
        (await Page.Locator(EditableSurfaces).CountAsync()).ShouldBe(0);

        var outage = await Watch.DropConnectionAsync(Page);
        await outage.RestoreAsync();
        await Page.EvaluateAsync("() => window.answer()");

        await ShouldBeEditableAgainAsync();
    }
}
