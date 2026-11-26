using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// Every way of joining two blocks keeps all the text and its formatting, and can be undone. Firefox's own editing
/// does not: see <see cref="Control_ABareContentEditable_ShowsWhatTheEngineItselfDoes"/>.
/// </summary>
public class SurfaceJoinTests(SampleHost host) : SurfaceTest(host)
{
    private const string Inline = "x <code>CO</code> y <strong>BO</strong> <a href=\"/l\">LI</a> <em>EM</em> z";
    private const string First = "first x CO y BO LI EM z";
    private const string Second = "second x CO y BO LI EM z";

    // Replaces the element's content with two blocks, as stored content or the author's editing might leave them.
    private const string LoadScript = """
        (surface, markup) => {
            surface.focus();
            surface.innerHTML = markup;
            surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
        }
        """;

    // Selects from a character offset in one line (an element holding text) to one in another. -1 is the end.
    private const string SelectLinesScript = """
        (surface, [fromLine, from, toLine, to]) => {
            surface.focus();
            const lines = [...surface.querySelectorAll('p, h2, h3, li, blockquote')]
                .filter(e => !e.querySelector(':scope > p') && !(e.tagName === 'BLOCKQUOTE' && e.querySelector(':scope > ul')));
            const point = (line, at) => {
                const walker = document.createTreeWalker(lines[line], NodeFilter.SHOW_TEXT);
                let left = at < 0 ? Number.MAX_SAFE_INTEGER : at;
                let last;
                for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                    if (text.parentElement.closest('li, p, h2, h3, blockquote') !== lines[line]) continue;
                    last = text;
                    if (left <= text.length) return [text, left];
                    left -= text.length;
                }
                return last ? [last, last.length] : [lines[line], 0];
            };
            getSelection().setBaseAndExtent(...point(fromLine, from), ...point(toLine, to));
        }
        """;

    private static readonly string[] Operations =
        ["backspace", "delete", "delete-selection", "type", "paste", "paste-paragraphs", "cut", "enter", "shift-enter"];

    private static readonly int[] StartOfSecondLine = [1, 0, 1, 0];

    private static string Block(string kind, string inline) => kind switch
    {
        "p" => $"<p>{inline}</p>",
        "h2" => $"<h2>{inline}</h2>",
        "h3" => $"<h3>{inline}</h3>",
        "ul" => $"<ul><li>{inline}</li></ul>",
        "ol" => $"<ol><li>{inline}</li></ol>",
        "paragraph-item" => $"<ul><li data-rt-paragraph=\"\">{inline}</li></ul>",
        "quote" => $"<blockquote><p>{inline}</p></blockquote>",
        _ => $"<blockquote><ul><li>{inline}</li></ul></blockquote>",
    };

    private ILocator Start => Surface("start");

    [Theory]
    [InlineData("p", "h2")]
    [InlineData("h2", "p")]
    [InlineData("h2", "h3")]
    [InlineData("p", "ul")]
    [InlineData("ul", "p")]
    [InlineData("h2", "ul")]
    [InlineData("ol", "h3")]
    [InlineData("p", "quote")]
    [InlineData("quote", "p")]
    [InlineData("h2", "quote")]
    [InlineData("quote", "h2")]
    [InlineData("quote", "ul")]
    [InlineData("ul", "quote")]
    [InlineData("p", "quoted-list")]
    [InlineData("quoted-list", "p")]
    [InlineData("paragraph-item", "h2")]
    [InlineData("p", "p")]
    [InlineData("ul", "ul")]
    public async Task JoiningTwoBlocks_KeepsEverything_AndCanBeUndone(string first, string second)
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        var markup = Block(first, "first " + Inline) + Block(second, "second " + Inline);

        foreach (var operation in Operations)
        {
            await Start.EvaluateAsync(LoadScript, markup);
            var before = Nodes(await SaveAsync(Page), "start");
            Text(before).ShouldBe(First + Second, operation);

            var (expected, codes) = await ApplyAsync(operation);

            var after = Nodes(await SaveAsync(Page), "start");
            Text(after).ShouldBe(expected, $"{operation}: {Compact(after)}");
            Count(after, "inlineCode").ShouldBe(codes, $"{operation}: {Compact(after)}");
            Count(after, "strong").ShouldBe(codes, $"{operation}: {Compact(after)}");
            Count(after, "link").ShouldBe(codes, $"{operation}: {Compact(after)}");
            (await ErrorCodesAsync(Page)).ShouldBeEmpty(operation);

            // The browser's own undo takes it back, in as many steps as it took.
            await Start.FocusAsync();
            var undone = after;
            for (var step = 0; step < 4 && Compact(undone) != Compact(before); step++)
            {
                await Page.EvaluateAsync("() => document.execCommand('undo')");
                undone = Nodes(await SaveAsync(Page), "start");
                await Start.FocusAsync();
            }

            Compact(undone).ShouldBe(Compact(before), operation);
        }
    }

    // Returns the text expected afterwards, and how many of the two runs of each formatting.
    private async Task<(string Text, int Runs)> ApplyAsync(string operation)
    {
        var joined = First + Second;
        var cut = First[..3] + Second[3..];
        switch (operation)
        {
            case "backspace":
                await SelectLinesAsync(1, 0, 1, 0);
                await Page.Keyboard.PressAsync("Backspace");
                return (joined, 2);
            case "delete":
                await SelectLinesAsync(0, -1, 0, -1);
                await Page.Keyboard.PressAsync("Delete");
                return (joined, 2);
        }

        await SelectLinesAsync(0, 3, 1, 3);
        switch (operation)
        {
            case "delete-selection":
                await Page.Keyboard.PressAsync("Delete");
                return (cut, 1);
            case "type":
                await Page.Keyboard.TypeAsync("QR");
                return (First[..3] + "QR" + Second[3..], 1);
            case "paste":
                await PasteAsync("QR");
                return (First[..3] + "QR" + Second[3..], 1);
            case "paste-paragraphs":
                await PasteAsync("Q\n\nR");
                return (First[..3] + "QR" + Second[3..], 1);
            case "cut":
                await Page.Keyboard.PressAsync("ControlOrMeta+x");
                return (cut, 1);
            case "enter":
                await Page.Keyboard.PressAsync("Enter");
                return (cut, 1);
            default:
                await Page.Keyboard.PressAsync("Shift+Enter");
                return (cut, 1);
        }
    }

    private async Task SelectLinesAsync(int fromLine, int from, int toLine, int to) =>
        await Start.EvaluateAsync(SelectLinesScript, new[] { fromLine, from, toLine, to });

    private async Task PasteAsync(string text) =>
        await Start.EvaluateAsync(
            """
            (surface, text) => {
                const event = new Event('paste', { bubbles: true, cancelable: true });
                Object.defineProperty(event, 'clipboardData', { value: { getData: () => text } });
                surface.dispatchEvent(event);
            }
            """,
            text);

    private static string Text(JsonArray nodes) => string.Concat(nodes.Select(TextOf)).Replace(" ", " ");

    private static int Count(JsonNode? node, string kind) => node switch
    {
        JsonArray array => array.Sum(child => Count(child, kind)),
        JsonObject o => ((string?)o["kind"] == kind ? 1 : 0) + Count(o["children"], kind),
        _ => 0,
    };

    [Theory]
    [InlineData("h2", "ul")]
    [InlineData("p", "h2")]
    public async Task ReplacingAJoinAsAnInputMethodStarts_KeepsTheRest(string first, string second)
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await Start.EvaluateAsync(LoadScript, Block(first, "first " + Inline) + Block(second, "second " + Inline));
        await SelectLinesAsync(0, 3, 1, 3);

        // What can be shown without a real input method: the join is done as the composition starts, where needed.
        await Start.EvaluateAsync("""
            surface => {
                surface.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
                surface.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }));
            }
            """);

        var expected = Host.Browser.BrowserType.Name == "firefox" ? First[..3] + Second[3..] : First + Second;
        Text(Nodes(await SaveAsync(Page), "start")).ShouldBe(expected);
    }

    // The same joins in a contenteditable element that this library has nothing to do with. Only Firefox loses text.
    // If this starts failing there, the engine no longer does, and the replacement of its joins can go.
    [Fact]
    public async Task Control_ABareContentEditable_ShowsWhatTheEngineItselfDoes()
    {
        await OpenFixtureAsync("empty", "write");
        await Page.EvaluateAsync("""
            () => {
                const bare = document.createElement('div');
                bare.id = 'bare';
                bare.contentEditable = 'true';
                bare.style.whiteSpace = 'pre-wrap';
                document.body.append(bare);
            }
            """);
        var bare = Page.Locator("#bare");

        await bare.EvaluateAsync(
            "(element, markup) => { element.innerHTML = markup; }",
            Block("h2", "first " + Inline) + Block("p", "second " + Inline));
        await bare.EvaluateAsync(SelectLinesScript, StartOfSecondLine);
        await Page.Keyboard.PressAsync("Backspace");

        var kept = (await bare.TextContentAsync())!.Replace(" ", " ");
        if (Host.Browser.BrowserType.Name == "firefox")
        {
            kept.ShouldStartWith(First);
            kept.Length.ShouldBeLessThan(First.Length + Second.Length, "Firefox kept everything in its own join");
        }
        else
        {
            kept.ShouldBe(First + Second);
        }
    }
}
