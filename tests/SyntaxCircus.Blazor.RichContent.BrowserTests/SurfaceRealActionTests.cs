using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// What only real input can show: the browser's own clipboard and drag and drop, with no dispatched events; and the
/// commands on a page whose own styles reach into the surface.
/// </summary>
[Collection(ClipboardTests.Name)]
public class SurfaceRealActionTests(SampleHost host) : SurfaceTest(host)
{
    private ILocator Start => Surface("start");

    private async Task LoadAsync(string markup) =>
        await Start.EvaluateAsync(
            """
            (surface, markup) => {
                surface.focus();
                surface.innerHTML = markup;
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """,
            markup);

    // Selects from an offset in one element's first text node to an offset in another's.
    private async Task SelectBetweenAsync(string from, int fromOffset, string to, int toOffset) =>
        await Start.EvaluateAsync(
            """
            (surface, [from, fromOffset, to, toOffset]) => {
                surface.focus();
                const text = selector => surface.querySelector(selector).firstChild;
                getSelection().setBaseAndExtent(text(from), Number(fromOffset), text(to), Number(toOffset));
            }
            """,
            new[] { from, $"{fromOffset}", to, $"{toOffset}" });

    // A plain text box of the page's own, to paste into.
    private async Task<ILocator> AddTextBoxAsync()
    {
        await Page.EvaluateAsync("""
            () => {
                const box = document.createElement('textarea');
                box.id = 'elsewhere';
                box.rows = 6;
                document.getElementById('fixture').prepend(box);
            }
            """);
        return Page.Locator("#elsewhere");
    }

    // ---- V1: the clipboard after a cut ----

    [Theory]
    // Within one block, which every browser does itself; and across blocks, which in Firefox is done by the surface.
    [InlineData("<p>alpha bravo</p><h2>charlie delta</h2>", "p", 0, "p", 5, "alpha", " bravo|charlie delta")]
    [InlineData("<p>alpha bravo</p><h2>charlie delta</h2>", "p", 6, "h2", 7, "bravo|charlie", "alpha  delta")]
    [InlineData("<h2>alpha bravo</h2><ul><li>charlie delta</li></ul>", "h2", 6, "li", 7, "bravo|charlie", "alpha  delta")]
    public async Task CuttingWithTheKeyboard_PutsTheTextOnTheClipboard(
        string markup, string from, int fromOffset, string to, int toOffset, string cut, string left)
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        var elsewhere = await AddTextBoxAsync();
        await LoadAsync(markup);
        await SelectBetweenAsync(from, fromOffset, to, toOffset);

        await Page.Keyboard.PressAsync("ControlOrMeta+x");

        var kept = Nodes(await SaveAsync(Page), "start").Select(TextOf);
        string.Join('|', kept).ShouldBe(left);
        await elsewhere.ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+v");
        var pasted = (await elsewhere.InputValueAsync()).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string.Join('|', pasted.Select(line => line.Trim())).ShouldBe(cut);
    }

    [Fact]
    public async Task Paste_FromTheClipboard_TakesOnlyTheText()
    {
        await OpenFixtureAsync("empty", "write");
        await Page.EvaluateAsync("""
            () => {
                const source = document.createElement('div');
                source.id = 'source';
                source.contentEditable = 'true';
                const heading = document.createElement('h1');
                const bold = document.createElement('b');
                bold.textContent = 'rich words';
                heading.append(bold);
                source.append(heading);
                document.body.append(source);
            }
            """);
        await Page.Locator("#source").ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("ControlOrMeta+c");
        await Start.ClickAsync();

        await Page.Keyboard.PressAsync("ControlOrMeta+v");

        await Expect(Start).ToContainTextAsync("rich words");
        await Expect(Start.Locator("b, h1")).ToHaveCountAsync(0);
        await ShouldSaveAsync("pasted", Wrap("paragraph", T("rich words")));
    }

    // ---- V2: dragging text ----

    private async Task DragAsync(float fromX, float fromY, float toX, float toY)
    {
        await Page.Mouse.MoveAsync(fromX, fromY);
        await Page.Mouse.DownAsync();
        // A drag starts only once the pointer has travelled, and over some time.
        await Page.WaitForTimeoutAsync(300);
        await Page.Mouse.MoveAsync(fromX + 10, fromY + 2, new() { Steps = 5 });
        await Page.Mouse.MoveAsync(toX, toY, new() { Steps = 20 });
        await Page.WaitForTimeoutAsync(100);
        await Page.Mouse.UpAsync();
        await Page.WaitForTimeoutAsync(300);
    }

    // Selects the element's first word, and returns the middle of it and a point at the end of the target's text.
    private static Task<float[]> SelectWordAsync(ILocator element, string targetSelector) =>
        element.EvaluateAsync<float[]>(
            """
            (element, targetSelector) => {
                element.closest('[contenteditable]').focus();
                const text = element.firstChild;
                const word = document.createRange();
                word.setStart(text, 0);
                word.setEnd(text, text.data.indexOf(' '));
                getSelection().removeAllRanges();
                getSelection().addRange(word);
                const from = word.getBoundingClientRect();
                const end = document.createRange();
                end.selectNodeContents(element.parentElement.querySelector(targetSelector));
                const rects = end.getClientRects();
                const to = rects[rects.length - 1];
                return [from.left + from.width / 2, from.top + from.height / 2, to.right + 4, to.top + to.height / 2];
            }
            """,
            targetSelector);

    [Fact]
    public async Task DraggingSelectedTextWithTheMouse_MovesNothing_AndDeletesNothing()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        // The same drag in an element the surface has nothing to do with shows whether this automation can drag here.
        await Page.EvaluateAsync("""
            () => {
                const bare = document.createElement('div');
                bare.id = 'bare';
                bare.contentEditable = 'true';
                for (const words of ['moving words here', 'target line']) {
                    const paragraph = document.createElement('p');
                    paragraph.textContent = words;
                    bare.append(paragraph);
                }
                document.getElementById('fixture').prepend(bare);
            }
            """);
        var control = await SelectWordAsync(Page.Locator("#bare p").First, "p:last-child");
        await DragAsync(control[0], control[1], control[2], control[3]);
        var canDrag = await Page.Locator("#bare").InnerTextAsync() is var after && after.Contains("target linemoving");
        if (!canDrag)
        {
            Assert.Skip($"This engine's automation does not drag text, even in a bare contenteditable: {after}");
        }

        await LoadAsync("<p>moving words here</p><p>target line</p>");
        var before = await SaveAsync(Page);
        var points = await SelectWordAsync(Start.Locator("p").First, "p:last-child");

        await DragAsync(points[0], points[1], points[2], points[3]);

        await Expect(Start.Locator("p")).ToHaveTextAsync(["moving words here", "target line"]);
        (await SaveAsync(Page)).ShouldBe(before);

        // Nor can it be dragged out, which would take it from the document.
        var elsewhere = await AddTextBoxAsync();
        points = await SelectWordAsync(Start.Locator("p").First, "p:last-child");
        var box = (await elsewhere.BoundingBoxAsync())!;
        await DragAsync(points[0], points[1], box.X + 20, box.Y + 20);

        await Expect(Start.Locator("p")).ToHaveTextAsync(["moving words here", "target line"]);
        (await SaveAsync(Page)).ShouldBe(before);

        // And what is dragged in from elsewhere on the page is neither taken nor lost from where it was.
        await Page.EvaluateAsync("""
            () => {
                document.querySelector('#bare p').textContent = 'outside words here';
                document.getElementById('elsewhere').remove();
            }
            """);
        points = await SelectWordAsync(Page.Locator("#bare p").First, "p:last-child");
        var target = await Start.Locator("p").Last.EvaluateAsync<float[]>(
            "p => { const r = document.createRange(); r.selectNodeContents(p); const b = r.getBoundingClientRect(); return [b.right + 4, b.top + b.height / 2]; }");

        await DragAsync(points[0], points[1], target[0], target[1]);

        await Expect(Page.Locator("#bare p").First).ToHaveTextAsync("outside words here");
        await Expect(Start.Locator("p")).ToHaveTextAsync(["moving words here", "target line"]);
        (await SaveAsync(Page)).ShouldBe(before);
    }

    // ---- V3: a page whose styles reach into the surface ----

    // What resets and themes commonly do, with an id so that they outrank a plain rule of the surface's own.
    private const string HostStyles = """
        #fixture * { font-family: serif; }
        #fixture h1, #fixture h2, #fixture h3, #fixture h4 { font-weight: 700; }
        #fixture p, #fixture li { font-weight: 600; font-style: italic; }
        #fixture blockquote { font-style: italic; font-weight: bold; }
        #fixture font { font: inherit; }
        #fixture b, #fixture strong { font-weight: inherit; }
        #fixture i, #fixture em { font-style: normal; }
        #fixture a { font-weight: bold; font-style: italic; }
        #fixture [contenteditable] { font: italic bold 16px/1.4 serif; }
        """;

    private static string T(string text) => $$"""{"kind":"text","text":"{{text}}"}""";

    private static string Wrap(string kind, params string[] children) =>
        $$"""{"kind":"{{kind}}","children":[{{string.Join(',', children)}}]}""";

    private static string Code(string text) => $$"""{"kind":"inlineCode","text":"{{text}}"}""";

    private async Task ShouldSaveAsync(string step, params string[] nodes)
    {
        var saved = Nodes(await SaveAsync(Page), "start");
        JsonNode.DeepEquals(saved, JsonNode.Parse($"[{string.Join(',', nodes)}]"))
            .ShouldBeTrue($"{step}: {Compact(saved)}");
    }

    private static async Task SelectWordsAsync(ILocator element, int from, int to) =>
        await element.EvaluateAsync(
            """
            (element, [from, to]) => {
                element.closest('[data-rt-surface]').focus();
                const point = (at, end) => {
                    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                    let left = at;
                    for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                        if (left < text.length || (end && left === text.length)) return [text, left];
                        left -= text.length;
                    }
                };
                getSelection().setBaseAndExtent(...point(from, from === to), ...point(to, true));
            }
            """,
            new[] { from, to });

    private ILocator Tool(string name) =>
        Page.GetByRole(AriaRole.Toolbar).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    [Theory]
    [InlineData("p")]
    [InlineData("h2")]
    [InlineData("quote")]
    public async Task HostStyles_DoNotChangeWhatTheCommandsDo(string kind)
    {
        await OpenFixtureAsync("empty", "write");
        await Page.AddStyleTagAsync(new() { Content = HostStyles });
        await Start.ClickAsync();
        var (markup, selector) = kind switch
        {
            "p" => ("<p>one two three</p>", "p"),
            "h2" => ("<h2>one two three</h2>", "h2"),
            _ => ("<blockquote><p>one two three</p></blockquote>", "blockquote p"),
        };
        await LoadAsync(markup);
        string Node(params string[] runs) => kind switch
        {
            "p" => Wrap("paragraph", runs),
            "h2" => $$"""{"kind":"heading","headingLevel":2,"children":[{{string.Join(',', runs)}}]}""",
            _ => Wrap("quote", Wrap("paragraph", runs)),
        };
        var line = Start.Locator(selector);
        var plain = Node(T("one two three"));

        foreach (var (button, wrapped) in new[]
        {
            ("Bold", Wrap("strong", T("two"))),
            ("Italic", Wrap("emphasis", T("two"))),
            ("Inline code", Code("two")),
        })
        {
            await SelectWordsAsync(line, 4, 7);
            // Nothing is formatted, whatever the page's styles make it look like.
            await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "false");

            await Tool(button).ClickAsync();

            await ShouldSaveAsync($"{button} on", Node(T("one "), wrapped, T(" three")));
            await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "true");

            await Tool(button).ClickAsync();

            await ShouldSaveAsync($"{button} off", plain);
            await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "false");
        }

        // Typed with the shortcut at the cursor, and ended with it.
        await SelectWordsAsync(line, 13, 13);
        await Page.Keyboard.PressAsync("ControlOrMeta+e");
        await Page.Keyboard.TypeAsync("x");
        await Page.Keyboard.PressAsync("ControlOrMeta+e");
        await Page.Keyboard.PressAsync("ControlOrMeta+b");
        await Page.Keyboard.TypeAsync("y");
        await Page.Keyboard.PressAsync("ControlOrMeta+b");
        await Page.Keyboard.TypeAsync("z");

        await ShouldSaveAsync("typed", Node(T("one two three"), Code("x"), Wrap("strong", T("y")), T("z")));
    }
}
