using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>The toolbar, the keyboard, links, paste, drop and undo in Write mode.</summary>
public class SurfaceCommandTests(SampleHost host) : SurfaceTest(host)
{
    // Selects a stretch of the element's text, by character offsets.
    private const string SelectScript = """
        (element, [from, to]) => {
            element.closest('[data-rt-surface]').focus();
            const point = at => {
                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                let left = at < 0 ? element.textContent.length : at;
                for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                    if (left <= text.length) return [text, left];
                    left -= text.length;
                }
                return [element, 0];
            };
            getSelection().setBaseAndExtent(...point(from), ...point(to));
        }
        """;

    private ILocator Start => Surface("start");

    private ILocator Toolbar => Page.GetByRole(AriaRole.Toolbar);

    private ILocator Tool(string name) => Toolbar.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private ILocator StyleSelect => Toolbar.GetByLabel("Text style");

    private ILocator LinkAddress => Page.GetByLabel("Link address");

    private static async Task SelectAsync(ILocator element, int from, int to) =>
        await element.EvaluateAsync(SelectScript, new[] { from, to });

    // The box closes only once the browser has been told to make the link.
    private async Task ApplyLinkAsync(string address)
    {
        await LinkAddress.FillAsync(address);
        await LinkAddress.PressAsync("Enter");
        await Expect(LinkAddress).ToHaveCountAsync(0);
    }

    private async Task OpenEmptyAsync(bool strictCsp = false)
    {
        await OpenFixtureAsync("empty", "write", strictCsp);
        await Start.ClickAsync();
    }

    private async Task<string[]> SavedAsync(string block = "start") => CompactAll(Nodes(await SaveAsync(Page), block));

    private async Task ShouldSaveAsync(params string[] nodes) => (await SavedAsync()).ShouldBe(nodes);

    private static string T(string text) => Compact(new JsonObject { ["kind"] = "text", ["text"] = text });

    private static string P(params string[] children) => $$"""{"kind":"paragraph","children":[{{string.Join(',', children)}}]}""";

    private static string H(int level, params string[] children) =>
        $$"""{"kind":"heading","children":[{{string.Join(',', children)}}],"headingLevel":{{level}}}""";

    private static string Strong(params string[] children) => $$"""{"kind":"strong","children":[{{string.Join(',', children)}}]}""";

    private static string Em(params string[] children) => $$"""{"kind":"emphasis","children":[{{string.Join(',', children)}}]}""";

    private static string Code(string text) => $$"""{"kind":"inlineCode","text":"{{text}}"}""";

    private static string Link(string url, params string[] children) =>
        $$"""{"kind":"link","children":[{{string.Join(',', children)}}],"url":"{{url}}"}""";

    private static string Ul(params string[] items) => $$"""{"kind":"list","children":[{{string.Join(',', items)}}],"ordered":false}""";

    private static string Ol(params string[] items) => $$"""{"kind":"list","children":[{{string.Join(',', items)}}],"ordered":true}""";

    private static string Li(params string[] children) => $$"""{"kind":"listItem","children":[{{string.Join(',', children)}}]}""";

    private static string Quote(params string[] children) => $$"""{"kind":"quote","children":[{{string.Join(',', children)}}]}""";

    // Compares as JSON, whatever order the properties are written in.
    private static void ShouldBeNodes(string[] actual, params string[] expected) =>
        actual.Select(Canonical).ShouldBe(expected.Select(Canonical));

    private static string Canonical(string json) => Sorted(JsonNode.Parse(json))!.ToJsonString();

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        _ => node?.DeepClone(),
    };

    private async Task ShouldHaveNodesAsync(params string[] expected)
    {
        ShouldBeNodes(await SavedAsync(), expected);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    private async Task UndoAsync() => await Page.Keyboard.PressAsync("ControlOrMeta+z");

    private ILocator FirstBlock => Start.Locator(":scope > :first-child");

    // ---- One per command ----

    [Theory]
    [InlineData("Bold", 4, 7)]
    [InlineData("Bold", 0, 3)]
    [InlineData("Italic", 4, 7)]
    [InlineData("Italic", 0, 3)]
    [InlineData("Inline code", 4, 7)]
    [InlineData("Inline code", 0, 3)]
    public async Task InlineButtons_FormatTheSelection_AndRemoveItAgain(string button, int from, int to)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("one two three");
        await SelectAsync(FirstBlock, from, to);

        await Tool(button).ClickAsync();

        var word = "one two three"[from..to];
        string[] runs =
        [
            .. from > 0 ? new[] { T("one two three"[..from]) } : [],
            button switch { "Bold" => Strong(T(word)), "Italic" => Em(T(word)), _ => Code(word) },
            T("one two three"[to..]),
        ];
        await ShouldHaveNodesAsync(P(runs));
        await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool(button).ClickAsync();

        await ShouldHaveNodesAsync(P(T("one two three")));
        await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Theory]
    [InlineData("h2", 2)]
    [InlineData("h3", 3)]
    [InlineData("h4", 4)]
    public async Task Style_MakesAHeading_AndAParagraphAgain(string style, int level)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("Title here");

        await StyleSelect.SelectOptionAsync(style);

        await ShouldHaveNodesAsync(H(level, T("Title here")));
        await Expect(StyleSelect).ToHaveValueAsync(style);

        await StyleSelect.SelectOptionAsync("p");

        await ShouldHaveNodesAsync(P(T("Title here")));
    }

    [Fact]
    public async Task Style_OverSeveralParagraphs_KeepsThemApart()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("first");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("second");
        await SelectAsync(Start, 2, 8);

        await StyleSelect.SelectOptionAsync("h2");

        await ShouldHaveNodesAsync(H(2, T("first")), H(2, T("second")));
    }

    [Fact]
    public async Task Bold_InAHeading_IsStored()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("one two");
        await StyleSelect.SelectOptionAsync("h2");
        await SelectAsync(FirstBlock, 0, 3);

        await Tool("Bold").ClickAsync();

        await ShouldHaveNodesAsync(H(2, Strong(T("one")), T(" two")));
    }

    [Theory]
    [InlineData("Bulleted list", false)]
    [InlineData("Numbered list", true)]
    public async Task ListButtons_MakeAList_AndEndIt_KeepingEachLine(string button, bool ordered)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("first");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("second");
        await SelectAsync(Start, 2, 8);

        await Tool(button).ClickAsync();

        var list = ordered ? Ol(Li(T("first")), Li(T("second"))) : Ul(Li(T("first")), Li(T("second")));
        await ShouldHaveNodesAsync(list);
        await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool(button).ClickAsync();

        await ShouldHaveNodesAsync(P(T("first")), P(T("second")));
        await Expect(Tool(button)).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Theory]
    [InlineData("Bulleted list")]
    [InlineData("Numbered list")]
    public async Task EndingAList_BetweenOtherLines_LeavesLinesThatTakeEveryCommand(string button)
    {
        await OpenEmptyAsync();
        await PasteAsync("before\n\nfirst\n\nsecond\n\nafter");
        await SelectAsync(Start, "beforefi".Length, "beforefirstse".Length);
        await Tool(button).ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(2);
        await Tool(button).ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(0);

        await ShouldHaveNodesAsync(P(T("before")), P(T("first")), P(T("second")), P(T("after")));
        await Expect(Start.Locator("[style]")).ToHaveCountAsync(0);
        // Each is a paragraph on the screen too, not a line in some other element.
        (await Start.EvaluateAsync<string[]>(
            "s => [...s.querySelectorAll('p')].map(p => p.textContent)")).ShouldBe(["before", "first", "second", "after"]);

        await PlaceCaretAsync(Start.Locator("p").Nth(1), 2);
        await StyleSelect.SelectOptionAsync("h2");

        await ShouldHaveNodesAsync(P(T("before")), H(2, T("first")), P(T("second")), P(T("after")));
    }

    [Fact]
    public async Task EndingPartOfAList_LeavesLinesThatTakeEveryCommand()
    {
        await OpenEmptyAsync();
        await PasteAsync("before\r\n\r\nfirst\r\n\r\nafter");
        await PlaceCaretAsync(Start.Locator("p").Nth(1));
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("second");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);

        await ShouldHaveNodesAsync(P(T("before")), Ul(Li(T("first"))), P(T("second")), P(T("after")));
        (await Start.EvaluateAsync<string[]>(
            "s => [...s.querySelectorAll('p')].filter(p => !p.querySelector('ul')).map(p => p.textContent)"))
            .ShouldBe(["before", "second", "after"]);

        await Start.FocusAsync();
        await PlaceCaretAsync(Start.Locator("p", new() { HasText = "second" }).Last, 2);
        await StyleSelect.SelectOptionAsync("h2");

        await ShouldHaveNodesAsync(P(T("before")), Ul(Li(T("first"))), H(2, T("second")), P(T("after")));
    }

    [Fact]
    public async Task NumberedList_RoundTripsOrdered()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("step");

        await Tool("Numbered list").ClickAsync();
        await ShouldHaveNodesAsync(Ol(Li(T("step"))));
        await Tool("Bulleted list").ClickAsync();
        await ShouldHaveNodesAsync(Ul(Li(T("step"))));
        await Tool("Numbered list").ClickAsync();
        await ShouldHaveNodesAsync(Ol(Li(T("step"))));
    }

    [Fact]
    public async Task ListButton_OnAHeading_MakesAPlainItem()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("head");
        await StyleSelect.SelectOptionAsync("h2");

        await Tool("Bulleted list").ClickAsync();

        await ShouldHaveNodesAsync(Ul(Li(T("head"))));
    }

    [Fact]
    public async Task Quote_QuotesTheLines_AndUnquotesThem()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("first");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("second");
        await SelectAsync(Start, 2, 8);

        await Tool("Quote").ClickAsync();

        await ShouldHaveNodesAsync(Quote(P(T("first")), P(T("second"))));
        await Expect(Tool("Quote")).ToHaveAttributeAsync("aria-pressed", "true");
        await SelectAsync(Start, 2, 8);

        await Tool("Quote").ClickAsync();

        await ShouldHaveNodesAsync(P(T("first")), P(T("second")));
    }

    [Fact]
    public async Task Quote_BetweenOtherBlocks_IsOneQuote_AndTheBrowserUndoesAndRedoesItExactly()
    {
        await OpenEmptyAsync();
        await PasteAsync("before\n\nfirst\n\nsecond\n\nthird\n\nafter");
        await SelectAsync(Start, "beforefi".Length, "beforefirstsecondth".Length);
        var plain = await SavedAsync();
        string[] quoted = [P(T("before")), Quote(P(T("first")), P(T("second")), P(T("third"))), P(T("after"))];

        await Tool("Quote").ClickAsync();

        await ShouldHaveNodesAsync(quoted);
        await StepUntilAsync("ControlOrMeta+z", plain);
        await StepUntilAsync("ControlOrMeta+Shift+z", quoted);
    }

    [Fact]
    public async Task EnterInAQuote_MakesAParagraphInTheSameQuote()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("onetwo");
        await Tool("Quote").ClickAsync();
        await Expect(Start.Locator("blockquote")).ToHaveCountAsync(1);
        await PlaceCaretAsync(Start.Locator("blockquote"), 3);

        await Page.Keyboard.PressAsync("Enter");

        await ShouldHaveNodesAsync(Quote(P(T("one")), P(T("two"))));
        await PlaceCaretAsync(Start.Locator("blockquote p").Last);
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.TypeAsync("three");
        await ShouldHaveNodesAsync(Quote(P(T("one")), P(T("two")), P(T("three"))));
    }

    [Theory]
    [InlineData("X", new[] { "firstX", "second" })]
    [InlineData("X\n\nY", new[] { "firstX", "Y", "second" })]
    public async Task PasteInAQuote_StaysInTheOneQuote(string pasted, string[] lines)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("first");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("second");
        await SelectAsync(Start, 2, 8);
        await Tool("Quote").ClickAsync();
        await Expect(Start.Locator("blockquote p")).ToHaveCountAsync(2);
        await PlaceCaretAsync(Start.Locator("blockquote p").First);

        await PasteAsync(pasted);

        await ShouldHaveNodesAsync(Quote([.. lines.Select(line => P(T(line)))]));
    }

    [Fact]
    public async Task LinkBox_AddsEditsAndRemovesALink()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("see the docs now");
        await SelectAsync(FirstBlock, 8, 12);

        await Tool("Link").ClickAsync();
        await Expect(LinkAddress).ToBeFocusedAsync();
        await LinkAddress.FillAsync("https://example.com/docs");
        await LinkAddress.PressAsync("Enter");

        await Expect(LinkAddress).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("see the "), Link("https://example.com/docs", T("docs")), T(" now")));

        await PlaceCaretAsync(FirstBlock, 9);
        await Expect(Tool("Link")).ToHaveAttributeAsync("aria-pressed", "true");
        await Tool("Link").ClickAsync();
        await Expect(LinkAddress).ToHaveValueAsync("https://example.com/docs");
        await LinkAddress.FillAsync("/local/page");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" }).ClickAsync();
        await Expect(LinkAddress).ToHaveCountAsync(0);

        await ShouldHaveNodesAsync(P(T("see the "), Link("/local/page", T("docs")), T(" now")));

        await PlaceCaretAsync(FirstBlock, 9);
        await Tool("Link").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Remove link" }).ClickAsync();
        await Expect(LinkAddress).ToHaveCountAsync(0);

        await ShouldHaveNodesAsync(P(T("see the docs now")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveClickedRightAfterApplyingALink_HasTheLink(bool enter)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);
        await Tool("Link").ClickAsync();
        await LinkAddress.FillAsync("/quick");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" })).ToHaveAttributeAsync("data-rt-address", "/quick");

        if (enter)
        {
            await LinkAddress.PressAsync("Enter");
        }
        else
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" }).ClickAsync();
        }

        await ShouldHaveNodesAsync(P(Link("/quick", T("text"))));
        await Expect(LinkAddress).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AnAddressChangedSinceDotNetCheckedIt_IsCheckedAgain_BeforeItIsLinked()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);
        await Tool("Link").ClickAsync();
        await LinkAddress.FillAsync("/checked");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" })).ToHaveAttributeAsync("data-rt-address", "/checked");

        // Changed and applied in one task, before .NET can have answered for the new value.
        await LinkAddress.EvaluateAsync("""
            input => {
                input.value = 'javascript:window.ran=1';
                input.dispatchEvent(new InputEvent('input', { bubbles: true }));
                document.querySelector('[data-rt-link-apply]').click();
            }
            """);

        await Expect(Toolbar.Locator("..").GetByRole(AriaRole.Alert)).ToContainTextAsync("https:");
        await Expect(Start.Locator("a")).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("text")));
    }

    [Fact]
    public async Task AskingForTheLinkBox_WhileRenderingLagsBehind_OpensIt_AndKeepsTheCircuit()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);
        await Expect(Tool("Link")).ToBeEnabledAsync();

        // Many reports and then the request, all sent before the browser can acknowledge one render. Blazor then
        // holds renders back, so the box is asked for before it is drawn, while earlier renders are still completing.
        await Start.EvaluateAsync("""
            surface => {
                const { dotNetRef } = surface.richContentSurface;
                for (let i = 0; i < 40; i++) {
                    dotNetRef.invokeMethodAsync('SelectionChanged', {
                        bold: i % 2 === 0, italic: false, code: false, linkUrl: null,
                        style: 'p', list: 'none', quote: false, inListItem: false,
                    });
                }
                dotNetRef.invokeMethodAsync('LinkRequested');
            }
            """);

        await Expect(LinkAddress).ToBeFocusedAsync();
        await ApplyLinkAsync("/after");
        await ShouldHaveNodesAsync(P(Link("/after", T("text"))));
    }

    [Fact]
    public async Task Link_WithNothingSelected_InsertsTheAddress()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("go ");

        await Tool("Link").ClickAsync();
        await ApplyLinkAsync("https://example.com/");

        await ShouldHaveNodesAsync(P(T("go "), Link("https://example.com/", T("https://example.com/"))));
    }

    [Theory]
    [InlineData("/fresh")]
    [InlineData("")]
    public async Task Link_ReplacesOrRemovesAStoredUnsafeAddress(string address)
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        // As a stored unsafe address is shown: no live href.
        await Start.EvaluateAsync("""
            surface => {
                const link = document.createElement('a');
                link.setAttribute('data-rt-url', 'javascript:window.ran=1');
                link.textContent = 'bad';
                const paragraph = document.createElement('p');
                paragraph.append('a ', link, ' z');
                surface.replaceChildren(paragraph);
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        await PlaceCaretAsync(FirstBlock, 3);

        await Tool("Link").ClickAsync();
        if (address.Length > 0)
        {
            await ApplyLinkAsync(address);
            await ShouldHaveNodesAsync(P(T("a "), Link(address, T("bad")), T(" z")));
        }
        else
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = "Remove link" }).ClickAsync();
            await Expect(LinkAddress).ToHaveCountAsync(0);
            await ShouldHaveNodesAsync(P(T("a bad z")));
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("http://example.com/")]
    [InlineData("//evil.example/x")]
    [InlineData("https://user@evil.example/")]
    [InlineData("example.com")]
    [InlineData("https://example.com/a b")]
    public async Task LinkBox_RefusesUnsafeUrlWithReason(string address)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);

        await Tool("Link").ClickAsync();
        await LinkAddress.FillAsync(address);
        await LinkAddress.PressAsync("Enter");

        await Expect(Toolbar.Locator("..").GetByRole(AriaRole.Alert)).ToContainTextAsync("https:");
        await Expect(LinkAddress).ToBeVisibleAsync();
        await Expect(Start.Locator("a")).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("text")));

        // Escape closes the box and returns to the text, still selected.
        await LinkAddress.PressAsync("Escape");
        await Expect(LinkAddress).ToHaveCountAsync(0);
        await Expect(Start).ToBeFocusedAsync();
        (await Page.EvaluateAsync<string>("() => getSelection().toString()")).ShouldBe("text");
    }

    // ---- A link too long for the box ----

    // A host with a raised limit, and "text" linked to an address of this length.
    private async Task<string> OpenWithLinkOfLengthAsync(int length)
    {
        await OpenFixtureAsync("empty", "write&urlLimit=8000");
        await WaitForSurfacesAsync(Page);
        await Start.ClickAsync();
        await Page.Keyboard.TypeAsync("text");
        var address = "https://example.com/" + new string('a', length - 21) + "z";
        address.Length.ShouldBe(length);
        await SelectAsync(FirstBlock, 0, 4);
        // The script's own command: the box cannot take an address this long.
        await Start.EvaluateAsync(
            "async (surface, address) => (await import('/_content/SyntaxCircus.Blazor.RichContent/surface/surface.js')).command(surface, 'link', address)",
            address);
        await ShouldHaveNodesAsync(P(Link(address, T("text"))));
        await PlaceCaretAsync(Start.Locator("a"), 2);
        await Expect(Tool("Link")).ToHaveAttributeAsync("aria-pressed", "true");
        return address;
    }

    [Theory]
    [InlineData(4001)]
    [InlineData(5000)]
    public async Task LinkBox_OnALinkTooLongForIt_AppliesNothing_ButCanRemoveIt(int length)
    {
        var address = await OpenWithLinkOfLengthAsync(length);
        var box = Page.GetByRole(AriaRole.Group, new() { Name = "Link", Exact = true });

        // Opening the box and pressing Enter, as one looking at a link might.
        await Page.Keyboard.PressAsync("ControlOrMeta+k");
        await Expect(box.GetByRole(AriaRole.Alert)).ToContainTextAsync("Blocks mode");
        await LinkAddress.PressAsync("Enter");
        await Expect(LinkAddress).ToBeVisibleAsync();
        await ShouldHaveNodesAsync(P(Link(address, T("text"))));

        await box.GetByRole(AriaRole.Button, new() { Name = "Apply link" }).ClickAsync();
        await Expect(LinkAddress).ToBeVisibleAsync();
        await ShouldHaveNodesAsync(P(Link(address, T("text"))));

        await LinkAddress.FillAsync("https://example.com/typed-afresh");
        await LinkAddress.PressAsync("Enter");
        await ShouldHaveNodesAsync(P(Link(address, T("text"))));

        await box.GetByRole(AriaRole.Button, new() { Name = "Remove link" }).ClickAsync();
        await ShouldHaveNodesAsync(P(T("text")));
    }

    [Fact]
    public async Task LinkBox_OnALinkOfExactlyTheMostItHolds_WorksAsEver()
    {
        var address = await OpenWithLinkOfLengthAsync(4000);

        await Page.Keyboard.PressAsync("ControlOrMeta+k");
        await Expect(LinkAddress).ToHaveValueAsync(address);
        await Expect(Page.GetByRole(AriaRole.Group, new() { Name = "Link", Exact = true }).GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await LinkAddress.PressAsync("Enter");

        await Expect(LinkAddress).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(Link(address, T("text"))));
    }

    // ---- Code in the shapes a browser's editing leaves ----

    // Every character of the surface, and of what is stored, with those that are code between « and ».
    private const string ShownCodeScript = """
        surface => {
            const walker = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT);
            let shown = '';
            for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                const code = getComputedStyle(text.parentElement).fontFamily.split(',')[0].replace(/["']/g, '').trim() === 'rt-code';
                shown += code ? '«' + text.data + '»' : text.data;
            }

            return shown.replaceAll('»«', '');
        }
        """;

    private static string Marked(JsonNode? node) =>
        (string?)node!["kind"] == "inlineCode" ? "«" + (string)node["text"]! + "»"
        : (string?)node["text"] ?? string.Concat(node["children"]?.AsArray().Select(Marked) ?? []);

    private async Task ShouldBeStoredAsShownAsync()
    {
        var shown = await Start.EvaluateAsync<string>(ShownCodeScript);
        var saved = Nodes(await SaveAsync(Page), "start");
        var stored = string.Concat(saved.Select(Marked)).Replace("»«", "", StringComparison.Ordinal);
        stored.ShouldBe(shown, await Start.InnerHTMLAsync() + Environment.NewLine + saved.ToJsonString());
    }

    // WebKit ends this with code in a <span> with a style, and in a <font> naming two families.
    [Fact]
    public async Task InlineCode_ThroughLeavingAListAndQuoting_IsStoredAsShown()
    {
        await OpenEmptyAsync();
        var keys = Page.Keyboard;
        await keys.TypeAsync("alpha");
        await keys.PressAsync("Enter");
        await keys.TypeAsync("beta");
        await keys.PressAsync("ControlOrMeta+b");
        await keys.TypeAsync("b");
        await keys.PressAsync("ControlOrMeta+b");
        await keys.PressAsync("ControlOrMeta+e");
        await keys.TypeAsync("c");
        await keys.PressAsync("ControlOrMeta+e");
        await Expect(Tool("Bulleted list")).ToBeEnabledAsync();
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await keys.PressAsync("End");
        await keys.PressAsync("Enter");
        await keys.PressAsync("Enter");
        await keys.TypeAsync("after");
        await ShouldBeStoredAsShownAsync();

        // Saving took the focus. Back in the text, the line is joined to what is above, then a new one is quoted.
        await Start.FocusAsync();
        await keys.PressAsync("Home");
        await keys.PressAsync("Backspace");
        await ShouldBeStoredAsShownAsync();

        await Start.FocusAsync();
        await keys.PressAsync("End");
        await keys.PressAsync("Enter");
        await keys.PressAsync("Enter");
        await keys.TypeAsync("q");
        await Expect(Tool("Quote")).ToBeEnabledAsync();
        await Tool("Quote").ClickAsync();
        await Expect(Start.Locator("blockquote")).ToHaveCountAsync(1);

        await ShouldBeStoredAsShownAsync();
        (await Start.EvaluateAsync<string>(ShownCodeScript)).ShouldContain("«");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    // Keys of a flow: "^e" is Ctrl/Cmd+E, "{Enter}" a key, and anything else is typed.
    private async Task PlayAsync(params string[] steps)
    {
        foreach (var step in steps)
        {
            if (step.StartsWith('^'))
            {
                await Page.Keyboard.PressAsync("ControlOrMeta+" + step[1..]);
            }
            else if (step.StartsWith('{'))
            {
                await Page.Keyboard.PressAsync(step[1..^1]);
            }
            else
            {
                await Page.Keyboard.TypeAsync(step);
            }
        }
    }

    // For each character stored as plain, whatever would make it look like code: the code font on the element that draws
    // it, or a box drawn by that element or any around it, up to the surface. A box covers all that is inside it, so
    // nothing the text itself is given could undo one.
    private const string PlainTextMarksScript = """
        (surface, [length, plain]) => {
            const marks = new Set();
            const walker = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT);
            let at = 0;
            for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                for (let i = 0; i < text.data.length; i++, at++) {
                    if (!plain[at]) {
                        continue;
                    }

                    const say = what => marks.add(`"${text.data[i]}" at ${at}: ${what}`);
                    if (getComputedStyle(text.parentElement).fontFamily.split(',')[0].replace(/["']/g, '').trim() === 'rt-code') {
                        say('in the code font');
                    }

                    for (let element = text.parentElement; element !== surface; element = element.parentElement) {
                        const style = getComputedStyle(element);
                        const name = element.outerHTML.slice(0, element.outerHTML.indexOf('>') + 1);
                        if (!/^(transparent|rgba\(0, 0, 0, 0\))$/.test(style.backgroundColor) || style.backgroundImage !== 'none') {
                            say(`${name} has the background ${style.backgroundColor} ${style.backgroundImage}`);
                        }

                        const bordered = ['Top', 'Right', 'Bottom', 'Left'].some(side =>
                            style[`border${side}Style`] !== 'none' && parseFloat(style[`border${side}Width`]) > 0);
                        if (bordered || style.outlineStyle !== 'none' || style.boxShadow !== 'none') {
                            say(`${name} has a border, an outline or a shadow`);
                        }
                    }
                }
            }

            return at === length ? [...marks] : [`the surface has ${at} characters, and ${length} are stored`];
        }
        """;

    private async Task ShouldNotMarkPlainTextAsCodeAsync()
    {
        var stored = string.Concat(Nodes(await SaveAsync(Page), "start").Select(Marked));
        var plain = new List<bool>();
        var code = false;
        foreach (var character in stored)
        {
            if (character is '«' or '»')
            {
                code = character == '«';
            }
            else
            {
                plain.Add(!code);
            }
        }

        plain.ShouldContain(true);
        (await Start.EvaluateAsync<string[]>(PlainTextMarksScript, new object[] { plain.Count, plain }))
            .ShouldBeEmpty(await Start.InnerHTMLAsync());
    }

    // Where Firefox writes the "plain" face inside the code, rather than splitting the code around what is typed.
    [Theory]
    [InlineData("spaces-after", new[] { "ab ", "^e", "cd", "^e", " XYZ" })]
    // Code switched off at the end of code, then more typing.
    [InlineData("off-at-end", new[] { "ab", "^e", "cd", "^e", "X" })]
    // Bold within code.
    [InlineData("bold-inside", new[] { "ab", "^e", "cd", "^b", "ef", "^b", "gh", "^e", "X" })]
    [InlineData("bold-then-off", new[] { "ab", "^e", "^b", "cd", "^e", "X", "^b", "Y" })]
    // Two lines of code joined, with plain text typed after code is switched off.
    [InlineData("joined-lines", new[] { "^e", "one", "{Enter}", "two", "{Home}", "{Backspace}", "{End}", "^e", "X" })]
    [InlineData("joined-then-off", new[] { "ab", "^e", "cd", "{Enter}", "ef", "^e", "gh", "{Home}", "{Backspace}", "{End}", "^e", "ij", "^e", "X" })]
    // Code switched on again from within what was typed after it.
    [InlineData("on-again", new[] { "ab", "^e", "cd", "^e", "X", "^e", "ef", "^e", "Y" })]
    public async Task TypingAfterInlineCode_IsStoredAsShown(string flow, string[] steps)
    {
        await OpenEmptyAsync();

        await PlayAsync(steps);

        await ShouldBeStoredAsShownAsync();
        (await Start.EvaluateAsync<string>(ShownCodeScript)).ShouldContain("«", Case.Sensitive, flow);
        await ShouldNotMarkPlainTextAsCodeAsync();
        // And the toolbar agrees, with the cursor in what was typed last.
        await Start.FocusAsync();
        await Page.Keyboard.PressAsync("End");
        var endsInCode = (await Start.EvaluateAsync<string>(ShownCodeScript)).EndsWith('»');
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", endsInCode ? "true" : "false");
    }

    // A host with fonts of its own for the editor, its headings, links, quotes and table cells.
    private async Task UseHostFontsAsync() => await Page.EvaluateAsync("""
        () => {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(`
                .doc-editor { font-family: Georgia, serif; }
                [data-rt-surface] h2 { font-family: "Courier New", serif; }
                [data-rt-surface] a { font-family: Verdana, serif; }
                [data-rt-surface] blockquote { font-family: Tahoma, serif; }
                [data-rt-surface] p.other { font-family: "Trebuchet MS", serif; }
            `);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
        }
        """);

    // For each marked element: its font, its parent's and its surface's, as computed.
    private const string FontsScript = """
        surface => [...surface.querySelectorAll('[id^=probe]')].map(element => [
            element.id,
            getComputedStyle(element).fontFamily,
            getComputedStyle(element.parentElement).fontFamily,
            getComputedStyle(surface).fontFamily,
        ])
        """;

    private async Task ShouldReadAsPlainAsync(ILocator surface, string id)
    {
        var probe = surface.Locator("#" + id);
        await PlaceCaretAsync(probe, 1);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(Tool("Bold")).ToBeEnabledAsync();
        await SelectAsync(probe, 0, 2);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    // The face that takes text out of code looks like what is around it where nothing around it is code, and like the
    // surface's own text where it is inside code, so that it never looks like code.
    [Fact]
    public async Task TheFaceThatTakesTextOutOfCode_NeverLooksLikeCode_AndFollowsTheHostsFonts()
    {
        const string Plain = "<font face=\"rt-plain\" id=";
        await OpenEmptyAsync();
        await UseHostFontsAsync();
        await Start.EvaluateAsync(
            "(surface, markup) => { surface.innerHTML = markup; surface.dispatchEvent(new InputEvent('input', { bubbles: true })); }",
            $"<p>a {Plain}\"probe-p\">in a paragraph</font></p>"
            + $"<p class=\"other\">b {Plain}\"probe-other\">in another font</font></p>"
            + $"<h2>c {Plain}\"probe-h\">in a heading</font></h2>"
            + $"<p><a href=\"/x\">d {Plain}\"probe-a\">in a link</font></a></p>"
            + $"<blockquote><p>e {Plain}\"probe-q\">in a quote</font></p></blockquote>"
            + $"<p><font face=\"rt-code\">f {Plain}\"probe-in-code\">in code</font></font></p>"
            + $"<p><font face=\"rt-code, monospace\">g {Plain}\"probe-in-restyled\">in restyled code</font></font></p>"
            + $"<p><span style=\"font-family: rt-code, monospace\"><b>h {Plain}\"probe-in-span\">in a span</font></b></span></p>"
            + $"<h2><font face=\"rt-code\">i {Plain}\"probe-in-heading-code\">in code in a heading</font></font></h2>");
        // The surface's font is taken as it gets the focus, which it had before the fonts changed here.
        await Start.BlurAsync();
        await Start.FocusAsync();

        var fonts = await Start.EvaluateAsync<string[][]>(FontsScript);

        fonts.Length.ShouldBe(9);
        foreach (var font in fonts)
        {
            var (id, own, parent, surface) = (font[0], font[1], font[2], font[3]);
            surface.ShouldStartWith("Georgia", Case.Sensitive, "the surface takes the host's font");
            own.ShouldBe(id.StartsWith("probe-in-", StringComparison.Ordinal) ? surface : parent, id);
        }

        await ShouldBeStoredAsShownAsync();
        foreach (var font in fonts)
        {
            await ShouldReadAsPlainAsync(Start, font[0]);
        }

        // A host that changes its font: what is typed from then on is shown in the new one.
        await Page.EvaluateAsync("() => document.querySelector('.doc-editor').style.fontFamily = 'Arial, sans-serif'");
        await SaveAsync(Page);
        await Start.FocusAsync();
        (await Start.Locator("#probe-in-code").EvaluateAsync<string>("element => getComputedStyle(element).fontFamily")).ShouldStartWith("Arial");
    }

    [Fact]
    public async Task TheFaceThatTakesTextOutOfCode_BehavesTheSameInATableCell()
    {
        await OpenFixtureAsync("everything", "write");
        await UseHostFontsAsync();
        var cell = Page.Locator("[data-block-id=prose] [data-rt-card=table] [data-rt-surface]").First;
        await cell.EvaluateAsync("""
            surface => {
                surface.innerHTML = 'a <font face="rt-plain" id="probe-cell">plain</font> '
                    + '<font face="rt-code">b <font face="rt-plain" id="probe-in-cell-code">out of code</font></font>';
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        await cell.FocusAsync();

        var fonts = await cell.EvaluateAsync<string[][]>(FontsScript);

        fonts.Length.ShouldBe(2);
        foreach (var font in fonts)
        {
            font[1].ShouldBe(font[3], font[0]);
            font[1].ShouldStartWith("Georgia", Case.Sensitive, font[0]);
        }

        foreach (var font in fonts)
        {
            await ShouldReadAsPlainAsync(cell, font[0]);
        }
    }

    // Puts markup in the live surface, as the browser's editing might leave it, and has it sent.
    private async Task LeaveMarkupAsync(string markup)
    {
        await OpenEmptyAsync();
        await Start.EvaluateAsync(
            "(surface, markup) => { surface.innerHTML = markup; surface.dispatchEvent(new InputEvent('input', { bubbles: true })); }",
            markup);
    }

    [Theory]
    [InlineData("<font face=\"rt-code, monospace\">two</font>")]
    [InlineData("<span style=\"font-family: rt-code, monospace;\">two</span>")]
    [InlineData("<font face=\"rt-code\">t<font face=\"rt-code, monospace\">w</font>o</font>")]
    [InlineData("<font face=\"rt-code\"><span style=\"font-family: rt-code, monospace;\">two</span></font>")]
    public async Task TheCodeButton_ShowsAndRemovesCode_InEveryShape(string code)
    {
        await LeaveMarkupAsync($"<p>one {code} three</p>");
        await ShouldBeStoredAsShownAsync();
        await SelectAsync(FirstBlock, 4, 7);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool("Inline code").ClickAsync();

        await ShouldHaveNodesAsync(P(T("one two three")));
        await ShouldBeStoredAsShownAsync();
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    // Part of a run, where a browser may split the element or put another inside it.
    [Theory]
    [InlineData("<font face=\"rt-code\">two</font>")]
    [InlineData("<font face=\"rt-code, monospace\">two</font>")]
    [InlineData("<span style=\"font-family: rt-code, monospace;\">two</span>")]
    public async Task RemovingCode_FromPartOfARun_IsStoredAsShown(string code)
    {
        await LeaveMarkupAsync($"<p>one {code} three</p>");
        await SelectAsync(FirstBlock, 5, 6);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool("Inline code").ClickAsync();

        await ShouldHaveNodesAsync(P(T("one "), Code("t"), T("w"), Code("o"), T(" three")));
        await ShouldBeStoredAsShownAsync();
    }

    [Theory]
    [InlineData("<b><i><font face=\"rt-code, monospace\">two</font></i></b>")]
    [InlineData("<b><i><span style=\"font-family: rt-code, monospace;\">two</span></i></b>")]
    [InlineData("<span style=\"font-family: rt-code, monospace;\"><b><i>two</i></b></span>")]
    [InlineData("<font face=\"rt-code, monospace\"><b><i>two</i></b></font>")]
    public async Task RemovingCode_InEveryShape_KeepsBoldAndItalic(string code)
    {
        await LeaveMarkupAsync($"<p>one {code} three</p>");
        await SelectAsync(FirstBlock, 4, 7);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool("Inline code").ClickAsync();

        await ShouldBeStoredAsShownAsync();
        var paragraph = (await SavedAsync()).ShouldHaveSingleItem();
        paragraph.ShouldNotContain("inlineCode");
        // Either may be around the other.
        Canonical(paragraph).ShouldBeOneOf(
            Canonical(P(T("one "), Strong(Em(T("two"))), T(" three"))),
            Canonical(P(T("one "), Em(Strong(T("two"))), T(" three"))));
    }

    // A list that is closed takes the arrows as a new choice. In the toolbar that would restyle the paragraph and send
    // the author back to the text, with the buttons after the list never reached.
    [Fact]
    public async Task Toolbar_ArrowsAlongIt_PassTheStyleList_WithoutChangingTheText()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await Expect(Tool("Bulleted list")).ToBeEnabledAsync();
        await Page.Keyboard.PressAsync("Alt+F10");
        await Expect(Tool("Bold")).ToBeFocusedAsync();
        var list = Toolbar.GetByRole(AriaRole.Combobox, new() { Name = "Text style" });

        (string Key, ILocator Reaches)[] steps =
        [
            ("ArrowRight", Tool("Italic")),
            ("ArrowRight", Tool("Inline code")),
            ("ArrowRight", Tool("Link")),
            ("ArrowRight", list),
            ("ArrowRight", Tool("Bulleted list")),
            ("ArrowLeft", list),
            ("ArrowLeft", Tool("Link")),
        ];
        foreach (var (key, reaches) in steps)
        {
            await Page.Keyboard.PressAsync(key);
            await Expect(reaches).ToBeFocusedAsync();
        }

        await ShouldHaveNodesAsync(P(T("text")));
    }

    // The browser takes what is code from how text looks, so text that is not code must not look it: not even through a
    // style attribute a command has written.
    private async Task ShouldNotLookLikeCodeOutsideCodeAsync(string what) =>
        (await Start.EvaluateAsync<string[]>(
            """
            surface => [...surface.querySelectorAll('*')]
                .filter(element => element.closest('code, font[face]')?.getAttribute('face') !== 'rt-code'
                    && element.closest('code') === null
                    && getComputedStyle(element).fontFamily.includes('rt-code'))
                .map(element => element.outerHTML)
            """)).ShouldBeEmpty(what);

    [Theory]
    [MemberData(nameof(CodeRemovals))]
    public async Task RemovingCode_ChangesOnlyTheCode_AndCanBeUndone(string name, string markup, int from, int to, string[] expected)
    {
        await OpenEmptyAsync();
        await Start.EvaluateAsync(
            "(s, markup) => { s.innerHTML = markup; s.dispatchEvent(new InputEvent('input', { bubbles: true })); }", markup);
        var before = await SavedAsync();
        await SelectAsync(FirstBlock, from, to);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");

        await Tool("Inline code").ClickAsync();

        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        var after = await SavedAsync();
        after.Select(Canonical).ShouldBe(expected.Select(Canonical), name);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        await ShouldNotLookLikeCodeOutsideCodeAsync(name);

        // One step of the browser's own undo takes it all back, exactly.
        await Start.FocusAsync();
        await UndoAsync();
        (await SavedAsync()).ShouldBe(before, name);
    }

    private const string CodeStart = "<font face=\"rt-code\">";
    private const string CodeEnd = "</font>";

    private static string Coded(string inner) => CodeStart + inner + CodeEnd;

    private static string LinkTo(string inner) => "<a href=\"/l\" data-rt-url=\"/l\">" + inner + "</a>";

    public static TheoryData<string, string, int, int, string[]> CodeRemovals => new()
    {
        {
            "partly bold", $"<p><b>a{Coded("bc")}</b>{Coded("de")}f</p>", 1, 5,
            [P(Strong(T("abc")), T("def"))]
        },
        {
            "partly italic, inside the code", $"<p>{Coded("<i>ab</i>cd")}</p>", 0, 4,
            [P(Em(T("ab")), T("cd"))]
        },
        {
            "bold and italic, each partly", $"<p><b>{Coded("ab")}</b><i>{Coded("cd")}</i>{Coded("<b><i>ef</i></b>gh")}</p>", 0, 8,
            [P(Strong(T("ab")), Em(T("cd")), Strong(Em(T("ef"))), T("gh"))]
        },
        {
            "code inside a link", $"<p>x {LinkTo(Coded("link"))} y</p>", 2, 6,
            [P(T("x "), Link("/l", T("link")), T(" y"))]
        },
        {
            "a link inside code", $"<p>{Coded("a " + LinkTo("<b>link</b>") + " b")}</p>", 0, 8,
            [P(T("a "), Link("/l", Strong(T("link"))), T(" b"))]
        },
        {
            "part of the code only", $"<p><b>{Coded("abcdef")}</b></p>", 2, 4,
            [P(Strong(Code("ab"), T("cd"), Code("ef")))]
        },
        {
            "formatted text and code around the selection are left alone",
            $"<p><b>aa </b>{Coded("bb")} cc {Coded("<i>dd</i>")}</p>", 3, 5,
            [P(Strong(T("aa ")), T("bb cc "), Em(Code("dd")))]
        },
    };

    [Fact]
    public async Task ApplyingCode_OverPartlyFormattedText_KeepsTheFormatting_AndRemovingItRestoresTheText()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("abcdef");
        await SelectAsync(FirstBlock, 1, 3);
        await Tool("Bold").ClickAsync();
        await Expect(Start.Locator("b, strong")).ToHaveCountAsync(1);
        await SelectAsync(FirstBlock, 3, 5);
        await Tool("Italic").ClickAsync();
        await Expect(Start.Locator("i, em")).ToHaveCountAsync(1);
        var formatted = await SavedAsync();
        await SelectAsync(FirstBlock, 0, 6);

        await Tool("Inline code").ClickAsync();

        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");
        await ShouldHaveNodesAsync(P(Code("a"), Strong(Code("bc")), Em(Code("de")), Code("f")));
        await SelectAsync(FirstBlock, 0, 6);

        await Tool("Inline code").ClickAsync();

        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        (await SavedAsync()).ShouldBe(formatted);
    }

    [Theory]
    // The formatted run in the middle of the code, and at its start; formatted before the code, and inside it after.
    [InlineData("Bold", 1, false)]
    [InlineData("Italic", 1, false)]
    [InlineData("Bold", 0, false)]
    [InlineData("Italic", 0, false)]
    [InlineData("Bold", 1, true)]
    [InlineData("Italic", 1, true)]
    [InlineData("Bold", 0, true)]
    [InlineData("Italic", 0, true)]
    public async Task AfterRemovingCode_TheCursorInFormattedText_IsNotInCode_AndCanMakeItCodeAgain(string format, int at, bool codeFirst)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("abcdef");
        if (codeFirst)
        {
            await SelectAsync(FirstBlock, 0, 6);
            await Tool("Inline code").ClickAsync();
            await Expect(Start.Locator("font")).ToHaveCountAsync(1);
        }

        await SelectAsync(FirstBlock, at, at + 2);
        await Tool(format).ClickAsync();
        await Expect(Start.Locator("b, strong, i, em")).ToHaveCountAsync(1);
        if (!codeFirst)
        {
            await SelectAsync(FirstBlock, 0, 6);
            await Tool("Inline code").ClickAsync();
        }

        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");
        await SelectAsync(FirstBlock, 0, 6);
        await Tool("Inline code").ClickAsync();
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        Func<string[], string> formatted = format == "Bold" ? Strong : Em;

        await PlaceCaretAsync(FirstBlock, at + 1);

        await Expect(Tool(format)).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        await Page.Keyboard.TypeAsync("X");
        var typed = "abcdef".Substring(at, 2).Insert(1, "X");
        string[] Around(string node) =>
            [.. at > 0 ? new[] { T("abcdef"[..at]) } : [], node, T("abcdef"[(at + 2)..])];
        await ShouldHaveNodesAsync(P(Around(formatted([T(typed)]))));
        await ShouldNotLookLikeCodeOutsideCodeAsync(format);

        await SelectAsync(FirstBlock, at, at + 3);
        await Tool("Inline code").ClickAsync();

        await ShouldHaveNodesAsync(P(Around(formatted([Code(typed)]))));
    }

    [Fact]
    public async Task CodeOverALink_KeepsTheLink()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("a link b");
        await SelectAsync(FirstBlock, 2, 6);
        await Tool("Link").ClickAsync();
        await ApplyLinkAsync("/x");
        await Expect(Start.Locator("a")).ToHaveCountAsync(1);
        await SelectAsync(FirstBlock, 0, 8);

        await Tool("Inline code").ClickAsync();

        await ShouldHaveNodesAsync(P(Code("a "), Link("/x", Code("link")), Code(" b")));
    }

    [Fact]
    public async Task Link_LongerThanTheUsualLimit_IsMade_WhenTheScriptIsAskedTo()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);

        // .NET checks the address against the host's limit, which can be above the usual one.
        (await CommandAsync(Start, "link", "/" + new string('a', 3000))).ShouldBeTrue();

        await Expect(Start.Locator("a")).ToHaveCountAsync(1);
        (await CommandAsync(Start, "link", "javascript:alert(1)")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("frozen", true)]
    [InlineData("frozen", false)]
    [InlineData("composing", true)]
    [InlineData("composing", false)]
    public async Task ALinkThatCannotBeMadeYet_LeavesTheBoxOpen_WithTheAddress(string why, bool enter)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);
        await Tool("Link").ClickAsync();
        await LinkAddress.FillAsync("/kept");
        var apply = Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" });
        await Expect(apply).ToHaveAttributeAsync("data-rt-address", "/kept");
        // The surface takes no command: the editor is being rearranged, or text is being composed in it.
        await Start.EvaluateAsync(
            """
            (surface, why) => {
                if (why === 'frozen') {
                    surface.closest('[data-rt-editor]').richContentFreeze.frozen = true;
                } else {
                    surface.dispatchEvent(new CompositionEvent('compositionstart'));
                }
            }
            """, why);

        await PressApplyAsync();

        // A round trip, by the end of which a box that was told to close has gone.
        await SaveAsync(Page);
        await SaveAsync(Page);
        await Expect(LinkAddress).ToHaveValueAsync("/kept");
        await Expect(Start.Locator("a")).ToHaveCountAsync(0);

        await Start.EvaluateAsync(
            """
            (surface, why) => {
                if (why === 'frozen') {
                    surface.closest('[data-rt-editor]').richContentFreeze.frozen = false;
                } else {
                    surface.dispatchEvent(new CompositionEvent('compositionend'));
                }
            }
            """, why);
        await PressApplyAsync();

        await Expect(LinkAddress).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(Link("/kept", T("text"))));

        Task PressApplyAsync() => enter ? LinkAddress.PressAsync("Enter") : apply.ClickAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingALink_WhereThereIsNone_ClosesTheBox(bool button)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);
        await Tool("Link").ClickAsync();
        await Expect(LinkAddress).ToHaveValueAsync("");

        if (button)
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = "Remove link" }).ClickAsync();
        }
        else
        {
            await LinkAddress.PressAsync("Enter");
        }

        await Expect(LinkAddress).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("text")));
    }

    // ---- Taking a step back ----

    // Stands in for one call of one of the browser's commands. "nothing" and "unrecorded" answer that it ran without
    // leaving an undo step, the second having changed the page all the same. "wrong" runs it for real, so that a step is
    // recorded, but with a result that is not the one asked for. Notes what the surface held at that call, and every
    // undo and redo from then on.
    private async Task StandInAsync(string command, string how) => await Start.EvaluateAsync(
        """
        (surface, [command, how]) => {
            const real = document.execCommand.bind(document);
            window.steps = [];
            window.held = null;
            document.execCommand = (name, ui, value) => {
                if (window.held !== null && (name === 'undo' || name === 'redo')) {
                    window.steps.push(name);
                }

                if (window.held !== null || name !== command || (command === 'formatBlock' && value !== 'blockquote')) {
                    return real(name, ui, value);
                }

                window.held = surface.innerHTML;
                if (how === 'wrong') {
                    return real(name, ui, command === 'formatBlock' ? 'h4' : value.replace('beta', 'beXta'));
                }

                if (how === 'unrecorded') {
                    const stray = document.createElement('p');
                    stray.textContent = 'stray';
                    const into = command === 'insertHTML'
                        ? surface.querySelector('blockquote') : null;
                    if (into) {
                        into.append(stray);
                    } else if (surface.querySelector('ul')) {
                        const item = document.createElement('li');
                        item.textContent = 'stray';
                        surface.querySelector('ul').append(item);
                    } else {
                        surface.append(stray);
                    }
                }

                return true;
            };
        }
        """, new[] { command, how });

    private Task<string[]> StepsAsync() => Page.EvaluateAsync<string[]>("() => window.steps");

    private void SkipWhereQuotesAreIndented(string what) => Assert.SkipWhen(
        what.StartsWith("quote", StringComparison.Ordinal) && Host.Browser.BrowserType.Name == "firefox",
        "Firefox quotes by indenting, which takes no step back.");

    // The author's typing is the step before the command's. A command that left none must not have it undone.
    private async Task ArrangeAsync(string what)
    {
        await OpenEmptyAsync();
        switch (what)
        {
            case "indent" or "paste":
                await Tool("Bulleted list").ClickAsync();
                await Page.Keyboard.TypeAsync("alpha");
                await Page.Keyboard.PressAsync("Enter");
                await Page.Keyboard.TypeAsync("beta typed");
                break;
            case "outdent":
                await Tool("Bulleted list").ClickAsync();
                await Page.Keyboard.TypeAsync("alpha");
                await Page.Keyboard.PressAsync("Enter");
                await Page.Keyboard.PressAsync("Tab");
                await Page.Keyboard.TypeAsync("beta typed");
                break;
            case "quote-heading" or "quote-lines-heading":
                await StyleSelect.SelectOptionAsync("h2");
                await Start.ClickAsync();
                await Page.Keyboard.TypeAsync("beta typed");
                break;
            case "quote-formatted":
                await Page.Keyboard.TypeAsync("alpha bold");
                await SelectAsync(FirstBlock, 6, 10);
                await Tool("Bold").ClickAsync();
                await PlaceCaretAsync(FirstBlock);
                await Page.Keyboard.PressAsync("Enter");
                await Page.Keyboard.TypeAsync("beta typed");
                await SelectAsync(Start, 0, -1);
                break;
            default:
                await Page.Keyboard.TypeAsync("beta typed");
                break;
        }
    }

    private async Task RunAsync(string what)
    {
        switch (what)
        {
            case "indent":
                await Page.Keyboard.PressAsync("Tab");
                break;
            case "outdent":
                await Page.Keyboard.PressAsync("Shift+Tab");
                break;
            case "paste":
                await PasteAsync(" one\n\ntwo");
                break;
            default:
                await Tool("Quote").ClickAsync();
                break;
        }
    }

    [Theory]
    [InlineData("indent", "insertHTML", "nothing")]
    [InlineData("indent", "insertHTML", "unrecorded")]
    [InlineData("outdent", "insertHTML", "unrecorded")]
    [InlineData("paste", "insertHTML", "unrecorded")]
    [InlineData("quote", "formatBlock", "nothing")]
    [InlineData("quote", "formatBlock", "unrecorded")]
    [InlineData("quote-heading", "formatBlock", "unrecorded")]
    [InlineData("quote-lines", "insertHTML", "nothing")]
    [InlineData("quote-lines", "insertHTML", "unrecorded")]
    [InlineData("quote-lines-heading", "insertHTML", "unrecorded")]
    public async Task ACommandThatLeftNoUndoStep_DoesNotUndoWhatCameBeforeIt(string what, string command, string how)
    {
        SkipWhereQuotesAreIndented(what);
        await ArrangeAsync(what);
        await StandInAsync(command, how);

        await RunAsync(what);

        await Page.WaitForFunctionAsync("() => window.held !== null");
        await Expect(Start).ToContainTextAsync("beta typed");
        var shown = await Start.InnerTextAsync();
        var saved = string.Concat(Nodes(await SaveAsync(Page), "start").Select(TextOf));
        saved.ShouldContain("beta typed", customMessage: shown);
    }

    [Theory]
    [InlineData("indent", "insertHTML")]
    [InlineData("outdent", "insertHTML")]
    [InlineData("paste", "insertHTML")]
    [InlineData("quote", "formatBlock")]
    [InlineData("quote-heading", "formatBlock")]
    [InlineData("quote", "insertHTML")]
    [InlineData("quote-formatted", "insertHTML")]
    [InlineData("quote-lines-heading", "insertHTML")]
    public async Task ACommandWithTheWrongResult_IsTakenBack_AndNotRedone(string what, string command)
    {
        SkipWhereQuotesAreIndented(what);
        await ArrangeAsync(what);
        await StandInAsync(command, "wrong");

        await RunAsync(what);

        await Page.WaitForFunctionAsync("() => window.held !== null");
        var steps = await StepsAsync();
        steps.ShouldNotContain("redo");
        // A quote is made in two steps, and both are taken back.
        steps.ShouldBe(what.StartsWith("quote", StringComparison.Ordinal) && command == "insertHTML" ? ["undo", "undo"] : ["undo"]);
        await Expect(Start).ToContainTextAsync("beta typed");
        await Expect(Start).Not.ToContainTextAsync("beXta");
        // The paste goes on to put its text in as a single line.
        if (what != "paste" && !(what.StartsWith("quote", StringComparison.Ordinal) && command == "insertHTML"))
        {
            (await Start.InnerHTMLAsync()).ShouldBe(await Page.EvaluateAsync<string>("() => window.held"));
        }

        await ShouldBeStoredAsShownAsync();
    }

    // ---- A nested editor ----

    [Fact]
    public async Task AnEditorInsideAnother_HasItsOwnToolbar_AndTheOuterOneLetsGo()
    {
        await OpenFixtureAsync("empty");
        await Page.EvaluateAsync(
            """
            async () => {
                const module = await import('/_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
                window.left = [];
                const make = name => {
                    const editor = document.createElement('div');
                    editor.setAttribute('data-rt-editor', '');
                    const area = document.createElement('div');
                    area.setAttribute('data-rt-toolbar-area', '');
                    const toolbar = document.createElement('div');
                    toolbar.setAttribute('data-rt-toolbar', '');
                    const bold = document.createElement('button');
                    bold.type = 'button';
                    bold.textContent = `${name} bold`;
                    bold.dataset.rtCommand = 'bold';
                    toolbar.append(bold);
                    area.append(toolbar);
                    const surface = document.createElement('div');
                    surface.id = name;
                    surface.setAttribute('data-rt-surface', 'prose');
                    editor.append(area, surface);
                    return { name, editor, area, surface };
                };
                const outer = make('outer');
                const inner = make('inner');
                // As in an extension block of the outer editor.
                const block = document.createElement('div');
                block.append(inner.editor);
                outer.editor.append(block);
                document.body.append(outer.editor);
                for (const { name, surface, area } of [outer, inner]) {
                    module.attach(surface, {
                        invokeMethodAsync(method) {
                            if (method === 'SurfaceLeft') window.left.push(name);
                            return Promise.resolve(true);
                        },
                    }, { profile: 'prose' });
                    const node = { kind: 'paragraph', children: [{ kind: 'text', text: `${name} text` }] };
                    module.sync(surface, [{ key: name, node }], false, true, 1);
                    module.attachToolbar(area, {
                        invokeMethodAsync(method, number) {
                            area.setAttribute('data-rt-drawn', number);
                            return Promise.resolve();
                        },
                    }, name);
                }
            }
            """);
        var outer = Page.Locator("#outer");
        var inner = Page.Locator("#inner");
        await Expect(outer).ToHaveAttributeAsync("contenteditable", "true");
        await Expect(Page.Locator("#outer:not([inert]), #inner:not([inert])")).ToHaveCountAsync(2);
        await SelectAsync(outer.Locator("p"), 0, 5);

        await PlaceCaretAsync(inner.Locator("p"), 2);

        await Page.WaitForFunctionAsync("() => window.left.includes('outer')");
        await Page.GetByRole(AriaRole.Button, new() { Name = "outer bold" }).ClickAsync();
        await Expect(outer.Locator("b, strong")).ToHaveCountAsync(0);

        await SelectAsync(inner.Locator("p"), 0, 5);
        await Page.GetByRole(AriaRole.Button, new() { Name = "inner bold" }).ClickAsync();
        await Expect(inner.Locator("b, strong")).ToHaveTextAsync("inner");

        await SelectAsync(outer.Locator("p"), 0, 5);
        await Page.GetByRole(AriaRole.Button, new() { Name = "outer bold" }).ClickAsync();
        await Expect(outer.Locator("b, strong")).ToHaveTextAsync("outer");
    }

    // ---- Keyboard ----

    [Fact]
    public async Task ShortcutsBIKE_Work()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("aa bb cc dd");

        await SelectAsync(FirstBlock, 0, 2);
        await Page.Keyboard.PressAsync("ControlOrMeta+b");
        await SelectAsync(FirstBlock, 3, 5);
        await Page.Keyboard.PressAsync("ControlOrMeta+i");
        await SelectAsync(FirstBlock, 6, 8);
        await Page.Keyboard.PressAsync("ControlOrMeta+e");
        await SelectAsync(FirstBlock, 9, 11);
        await Page.Keyboard.PressAsync("ControlOrMeta+k");
        await Expect(LinkAddress).ToBeFocusedAsync();
        await ApplyLinkAsync("/k");

        await ShouldHaveNodesAsync(P(Strong(T("aa")), T(" "), Em(T("bb")), T(" "), Code("cc"), T(" "), Link("/k", T("dd"))));
    }

    [Theory]
    [InlineData("ControlOrMeta+b")]
    [InlineData("ControlOrMeta+i")]
    [InlineData("ControlOrMeta+e")]
    public async Task AShortcutAtTheCursor_AppliesToWhatIsTypedNext_AndAgainEndsIt(string shortcut)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("a ");

        await Page.Keyboard.PressAsync(shortcut);
        await Page.Keyboard.TypeAsync("mid");
        await Page.Keyboard.PressAsync(shortcut);
        await Page.Keyboard.TypeAsync(" z");

        var formatted = shortcut[^1] switch { 'b' => Strong(T("mid")), 'i' => Em(T("mid")), _ => Code("mid") };
        await ShouldHaveNodesAsync(P(T("a "), formatted, T(" z")));
    }

    [Fact]
    public async Task Underline_IsNotApplied()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("text");
        await SelectAsync(FirstBlock, 0, 4);

        await Page.Keyboard.PressAsync("ControlOrMeta+u");

        await Expect(Start.Locator("u, [style]")).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("text")));
    }

    [Fact]
    public async Task EnterOnEmptyListItem_LeavesList()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("item");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("after");

        await ShouldHaveNodesAsync(Ul(Li(T("item"))), P(T("after")));
    }

    [Fact]
    public async Task EnterOnEmptyNestedListItem_LeavesOneLevel()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("top");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.TypeAsync("nested");
        await Page.Keyboard.PressAsync("Enter");

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("outer");

        await ShouldHaveNodesAsync(Ul(Li(T("top"), Ul(Li(T("nested")))), Li(T("outer"))));
    }

    [Fact]
    public async Task EnterOnEmptyQuoteLine_LeavesTheQuote()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("quoted");
        await Tool("Quote").ClickAsync();
        await Expect(Start.Locator("blockquote")).ToHaveCountAsync(1);

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("also");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("after");

        await ShouldHaveNodesAsync(Quote(P(T("quoted")), P(T("also"))), P(T("after")));
    }

    [Fact]
    public async Task ShiftEnter_MakesNewParagraph()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("one");
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.TypeAsync("two");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("item");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.TypeAsync("next");
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.TypeAsync("out");

        await ShouldHaveNodesAsync(P(T("one")), P(T("two")), Ul(Li(T("item")), Li(T("next"))), P(T("out")));
    }

    [Fact]
    public async Task TabNestsOnlyInsideList()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("one");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("two");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("three");
        await PlaceCaretAsync(Start.Locator("li").Nth(1), 1);

        await Page.Keyboard.PressAsync("Tab");

        await Expect(Start).ToBeFocusedAsync();
        // The caret stayed where it was in the text.
        await Page.Keyboard.TypeAsync("!");
        await ShouldHaveNodesAsync(Ul(Li(T("one"), Ul(Li(T("t!wo")))), Li(T("three"))));

        // Nesting the next one puts it beside the nested one, and again below it.
        await PlaceCaretAsync(Start.Locator("li").Nth(2), 2);
        await Page.Keyboard.PressAsync("Tab");
        await ShouldHaveNodesAsync(Ul(Li(T("one"), Ul(Li(T("t!wo")), Li(T("three"))))));
        await Start.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        await ShouldHaveNodesAsync(Ul(Li(T("one"), Ul(Li(T("t!wo"), Ul(Li(T("three"))))))));

        // Un-nesting an item with items below it takes them with it.
        await PlaceCaretAsync(Start.Locator("li").Nth(1), 1);
        await Page.Keyboard.PressAsync("Shift+Tab");
        await ShouldHaveNodesAsync(Ul(Li(T("one")), Li(T("t!wo"), Ul(Li(T("three"))))));
    }

    [Theory]
    [InlineData("Tab")]
    [InlineData("Shift+Tab")]
    public async Task Tab_OnStoredNestedLists_KeepsEveryItemAndOrdering(string key)
    {
        await OpenFixtureAsync("everything", "write");
        // "Nested numbered", in the ordered list inside the second item.
        var nested = Surface("prose").Locator(":scope > :nth-child(9) > li:nth-child(2) > ol > li").First;
        if (key == "Tab")
        {
            // The one after it can be nested below it without passing the depth limit; its own nested list cannot.
            await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(9) > li").Nth(1), 2);
        }
        else
        {
            await PlaceCaretAsync(nested, 2);
        }

        await Page.Keyboard.PressAsync(key);

        if (key == "Tab")
        {
            await Expect(Surface("prose")).Not.ToBeFocusedAsync();
        }
        else
        {
            await Expect(Surface("prose")).ToBeFocusedAsync();
        }

        var saved = await SaveAsync(Page);
        if (key == "Tab")
        {
            // Nesting it would take the list below the depth limit: nothing happens, and the focus moves on.
            saved.ShouldBe(Original("everything"));
        }
        else
        {
            var changed = AssertOnlyNodesReplaced(Original("everything"), saved, "prose", 8, 1, 1).Single()!;
            changed["children"]!.AsArray().Count.ShouldBe(3);
            var moved = changed["children"]![2]!;
            TextOf(moved).ShouldBe("Nested numberedDeeperOrdering not stored");
            Compact(moved).ShouldContain("\"ordered\":true");
            TextOf(changed).ShouldBe(TextOf(Nodes(Original("everything"), "prose")[8]));
        }

        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tab_CountsTheFormattingInsideCode_AgainstTheDepthLimit()
    {
        await OpenEmptyAsync();
        // As applying bold and italic inside code leaves it. Read as bold around italic around code, so nesting this
        // item once more would take the list past the limit.
        await Start.EvaluateAsync(
            """
            surface => {
                surface.innerHTML = '<ul><li>a<ul><li>b</li><li><font face="rt-code"><b><i>c</i></b></font></li></ul></li></ul>';
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        var before = await SavedAsync();
        await PlaceCaretAsync(Start.Locator("li li").Nth(1));

        await Page.Keyboard.PressAsync("Tab");

        await Expect(Start).Not.ToBeFocusedAsync();
        await Expect(Start.Locator("li li li")).ToHaveCountAsync(0);
        (await SavedAsync()).ShouldBe(before);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tab_KeepsAParagraphItemsForm()
    {
        await OpenFixtureAsync("everything", "write");
        var list = Surface("prose").Locator(":scope > :nth-child(10)");
        await PlaceCaretAsync(list.Locator(":scope > li").Nth(2), 3);

        await Page.Keyboard.PressAsync("Tab");

        var changed = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 9, 1, 1).Single()!;
        var items = changed["children"]!.AsArray();
        items.Count.ShouldBe(2);
        var second = items[1]!["children"]!.AsArray();
        second.Select(child => (string?)child!["kind"]).ShouldBe(["paragraph", "paragraph", "list"]);
        var moved = second[2]!["children"]!.AsArray().ShouldHaveSingleItem()!;
        moved["children"]!.AsArray().Select(child => (string?)child!["kind"]).ShouldBe(["paragraph", "list"]);
        TextOf(changed).ShouldBe(TextOf(Nodes(Original("everything"), "prose")[9]));
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Indenting_CanBeUndone_AndRedone()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("a");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("b");
        await Page.Keyboard.PressAsync("Tab");
        await ShouldHaveNodesAsync(Ul(Li(T("a"), Ul(Li(T("b"))))));

        await Start.FocusAsync();
        await UndoAsync();
        await ShouldHaveNodesAsync(Ul(Li(T("a")), Li(T("b"))));
        await Start.FocusAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+Shift+z");

        await ShouldHaveNodesAsync(Ul(Li(T("a"), Ul(Li(T("b"))))));
    }

    [Fact]
    public async Task TabOutsideList_MovesFocus()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > p").First, 3);

        await Page.Keyboard.PressAsync("Tab");

        await Expect(Surface("prose")).Not.ToBeFocusedAsync();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Fact]
    public async Task TabThatCannotNest_MovesFocus_SoTheKeyboardIsNeverTrapped()
    {
        await OpenFixtureAsync("everything", "write");
        // The first item of a top-level list can be neither nested nor un-nested.
        await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(9) > li").First, 3);

        await Page.Keyboard.PressAsync("Shift+Tab");

        await Expect(Surface("prose")).Not.ToBeFocusedAsync();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Fact]
    public async Task AltF10AndEscape_MoveToToolbarAndBack()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("keyboard only");
        await SelectAsync(FirstBlock, 0, 8);
        // The toolbar learns of the cursor through the server.
        await Expect(Tool("Bold")).ToBeEnabledAsync();

        await Page.Keyboard.PressAsync("Alt+F10");

        await Expect(Tool("Bold")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(Tool("Italic")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowLeft");
        await Page.Keyboard.PressAsync("Space");
        await Expect(Tool("Bold")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Start).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Alt+F10");
        await Page.Keyboard.PressAsync("Escape");

        await Expect(Start).ToBeFocusedAsync();
        (await Page.EvaluateAsync<string>("() => getSelection().toString()")).ShouldBe("keyboard");
        await ShouldHaveNodesAsync(P(Strong(T("keyboard")), T(" only")));
    }

    [Fact]
    public async Task RightArrowLeavesInlineCodeAndLink()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await Page.Keyboard.TypeAsync("a ");
        await Page.Keyboard.PressAsync("ControlOrMeta+e");
        await Page.Keyboard.TypeAsync("code");

        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "false");
        await Page.Keyboard.TypeAsync(" b ");
        await Page.Keyboard.TypeAsync("link");
        await SelectAsync(FirstBlock, "a code b ".Length, -1);
        await Tool("Link").ClickAsync();
        await ApplyLinkAsync("/x");
        await Expect(Start.Locator("a")).ToHaveCountAsync(1);
        await PlaceCaretAsync(FirstBlock, "a code b lin".Length);
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.TypeAsync(" c");

        await ShouldHaveNodesAsync(P(T("a "), Code("code"), T(" b "), Link("/x", T("link")), T(" c")));
    }

    [Fact]
    public async Task RightArrow_AtTheEndOfStoredCode_ThenAgain_MovesOn()
    {
        await OpenFixtureAsync("everything", "write");
        var heading = Surface("prose").Locator(":scope > h3");
        await PlaceCaretAsync(heading);

        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.TypeAsync("!");

        var changed = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 2, 1, 1).Single()!;
        ShouldBeNodes([Compact(changed)], H(3, T("A subheading with "), Code("code"), T("!")));

        await PlaceCaretAsync(heading, "A subheading with code".Length);
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.TypeAsync("?");
        (await heading.TextContentAsync()).ShouldBe("A subheading with code!");
    }

    // ---- Toolbar state ----

    [Fact]
    public async Task Toolbar_ShowsTheStateAtTheCursor_WithoutChangingTheDocument()
    {
        await OpenFixtureAsync("everything", "write");
        await Expect(Toolbar).ToHaveCountAsync(1);
        await Expect(Tool("Bold")).ToBeDisabledAsync();
        var first = Surface("prose").Locator(":scope > p").First;

        await first.Locator("strong").ClickAsync();
        await Expect(Tool("Bold")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Tool("Italic")).ToHaveAttributeAsync("aria-pressed", "false");
        await first.Locator("em").ClickAsync();
        await Expect(Tool("Italic")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Tool("Bold")).ToHaveAttributeAsync("aria-pressed", "false");
        await PlaceCaretAsync(first, "A plain paragraph with bold, italic, inli".Length);
        await Expect(Tool("Inline code")).ToHaveAttributeAsync("aria-pressed", "true");
        await PlaceCaretAsync(first.Locator("a"), 2);
        await Expect(Tool("Link")).ToHaveAttributeAsync("aria-pressed", "true");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"), 2);
        await Expect(StyleSelect).ToHaveValueAsync("h2");
        await Expect(Tool("Bold")).ToHaveAttributeAsync("aria-pressed", "false");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(10) li").First, 2);
        await Expect(Tool("Numbered list")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Tool("Bulleted list")).ToHaveAttributeAsync("aria-pressed", "false");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > blockquote > p").First, 2);
        await Expect(Tool("Quote")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(StyleSelect).ToBeDisabledAsync();
        await Expect(Tool("Quote")).ToBeEnabledAsync();

        // The same state in another surface is still that other surface's.
        await PlaceCaretAsync(Surface("more-prose").Locator("p"), 2);
        await Expect(StyleSelect).ToBeEnabledAsync();

        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Fact]
    public async Task Heading_DisabledInListItem()
    {
        await OpenFixtureAsync("everything", "write");

        await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(9) > li").First, 2);

        await Expect(StyleSelect).ToBeDisabledAsync();
        await Expect(Tool("Quote")).ToBeDisabledAsync();
        await Expect(Tool("Bulleted list")).ToBeEnabledAsync();
        // The script refuses too, whatever the toolbar shows.
        (await CommandAsync(Surface("prose"), "style", "h2")).ShouldBeFalse();
        (await CommandAsync(Surface("prose"), "quote")).ShouldBeFalse();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
    }

    [Fact]
    public async Task Disabled_DisablesTheToolbar()
    {
        await OpenFixtureAsync("everything", "write&disabled=true");
        await WaitForSurfacesAsync(Page);
        await Surface("prose").Locator(":scope > p").First.ClickAsync(new() { Force = true });

        foreach (var name in new[] { "Bold", "Italic", "Inline code", "Link", "Bulleted list", "Numbered list", "Quote" })
        {
            await Expect(Tool(name)).ToBeDisabledAsync();
        }

        await Expect(StyleSelect).ToBeDisabledAsync();
        (await CommandAsync(Surface("prose"), "bold")).ShouldBeFalse();
    }

    [Fact]
    public async Task BlocksMode_HasNoToolbar()
    {
        await OpenFixtureAsync("everything");

        await Expect(Toolbar).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task InlineProfile_HasNoBlockCommands()
    {
        await OpenFixtureAsync("empty");
        await Page.EvaluateAsync("""
            async () => {
                const module = await import('/_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
                const element = document.createElement('div');
                element.id = 'lab';
                element.setAttribute('data-rt-surface', 'inline');
                document.body.append(element);
                window.states = [];
                const component = {
                    invokeMethodAsync(method, state) {
                        if (method === 'SelectionChanged') window.states.push(state);
                        return Promise.resolve(true);
                    },
                };
                module.attach(element, component, { profile: 'inline' });
                module.sync(element, [{ key: 'i0', node: { kind: 'text', text: 'cell text' } }], false, true, 1);
                window.lab = { module, element };
            }
            """);
        var cell = Page.Locator("#lab");
        await PlaceCaretAsync(cell, 4);

        foreach (var (name, argument) in new[] { ("style", "h2"), ("bullets", ""), ("numbers", ""), ("quote", "") })
        {
            (await CommandAsync(cell, name, argument)).ShouldBeFalse(name);
        }

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.PressAsync("Tab");
        await PlaceCaretAsync(cell, 4);
        await PasteAsync("one\n\ntwo\nthree", "<h1>no</h1>");

        (await cell.InnerHTMLAsync()).ShouldBe("cellone two three text");
        await SelectAsync(cell, 0, 4);
        (await CommandAsync(cell, "bold")).ShouldBeTrue();
        await Expect(cell.Locator("b, strong")).ToHaveTextAsync("cell");
        (await Page.EvaluateAsync<bool>("() => window.states.length > 0 && window.states.every(s => s.style === 'p' && s.list === 'none')")).ShouldBeTrue();
    }

    private static Task<bool> CommandAsync(ILocator surface, string name, string argument = "") =>
        surface.EvaluateAsync<bool>(
            """
            async (surface, [name, argument]) => {
                const module = await import('/_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
                return module.command(surface, name, argument);
            }
            """,
            new[] { name, argument });

    // ---- Paste and drop ----

    private Task<bool> PasteAsync(string text, string html = "") =>
        Page.EvaluateAsync<bool>(
            """
            ([text, html]) => {
                const data = { getData: type => (type === 'text/plain' ? text : type === 'text/html' ? html : ''), types: ['text/plain', 'text/html'] };
                const event = new Event('paste', { bubbles: true, cancelable: true });
                Object.defineProperty(event, 'clipboardData', { value: data });
                const focused = document.activeElement;
                focused.dispatchEvent(event);
                return event.defaultPrevented;
            }
            """,
            new[] { text, html });

    [Fact]
    public async Task Paste_IgnoresHtml_SplitsOnBlankLines()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("onetwo");
        await PlaceCaretAsync(FirstBlock, 3);

        var prevented = await PasteAsync(
            " A\r\nstill A\r\n\r\n\r\n<img src=x onerror=window.pwned=1> B\n \t\nC ",
            "<h1 onclick=\"x()\">html</h1><img src=x onerror=window.pwned=1>");

        prevented.ShouldBeTrue();
        await Expect(Start.Locator("img, h1")).ToHaveCountAsync(0);
        await ShouldHaveNodesAsync(P(T("one A still A")), P(T("<img src=x onerror=window.pwned=1> B")), P(T("C two")));
        (await Page.EvaluateAsync<bool>("() => window.pwned === undefined")).ShouldBeTrue();
        // The caret is after what was pasted.
        await Start.FocusAsync();
        await Page.Keyboard.TypeAsync("|");
        await ShouldHaveNodesAsync(P(T("one A still A")), P(T("<img src=x onerror=window.pwned=1> B")), P(T("C |two")));

        // One step takes back what was typed, and one more the whole paste.
        await Start.FocusAsync();
        await UndoAsync();
        await UndoAsync();
        await ShouldHaveNodesAsync(P(T("onetwo")));
    }

    [Theory]
    [InlineData("x\ny", "onex ytwo")]
    [InlineData("  padded  ", "one  padded  two")]
    [InlineData("\n\nonly\n\n", "oneonlytwo")]
    public async Task Paste_OfOneParagraph_StaysInTheLine(string pasted, string expected)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("onetwo");
        await PlaceCaretAsync(FirstBlock, 3);

        await PasteAsync(pasted, "<p>no</p>");

        await ShouldHaveNodesAsync(P(T(expected)));
    }

    [Fact]
    public async Task Paste_IntoAnEmptyDocument_StoresOnlyTheParagraphs()
    {
        await OpenEmptyAsync();

        await PasteAsync("first\n\nsecond");

        await ShouldHaveNodesAsync(P(T("first")), P(T("second")));
    }

    [Fact]
    public async Task Paste_OverASelectionAcrossParagraphs_ReplacesIt()
    {
        await OpenEmptyAsync();
        await PasteAsync("alpha\n\nbravo\n\ncharlie");
        await SelectAsync(Start, 2, "alphabravocha".Length);

        await PasteAsync("X\n\nY");

        await ShouldHaveNodesAsync(P(T("alX")), P(T("Yrlie")));
    }

    [Fact]
    public async Task Paste_OfSeveralParagraphs_IntoAList_MakesItems()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("onetwo");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await PlaceCaretAsync(Start.Locator("li"), 3);

        await PasteAsync("X\n\nY");
        await Page.Keyboard.TypeAsync("|");

        await ShouldHaveNodesAsync(Ul(Li(T("oneX")), Li(T("Y|two"))));
    }

    [Fact]
    public async Task Drop_IsBlocked()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("kept");

        var prevented = await Start.EvaluateAsync<bool[]>("""
            surface => {
                const target = surface.firstElementChild;
                const data = new DataTransfer();
                data.setData('text/plain', 'dropped');
                data.setData('text/html', '<b>dropped</b>');
                const drag = type => {
                    const event = new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: data });
                    target.dispatchEvent(event);
                    return event.defaultPrevented;
                };
                const input = new InputEvent('beforeinput', { bubbles: true, cancelable: true, inputType: 'insertFromDrop' });
                target.dispatchEvent(input);
                return [drag('dragover'), drag('drop'), input.defaultPrevented];
            }
            """);

        prevented.ShouldBe([true, true, true]);
        await ShouldHaveNodesAsync(P(T("kept")));
    }

    // ---- Undo ----

    [Fact]
    public async Task Undo_And_Redo_ReachDocument()
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("plain");
        await SelectAsync(FirstBlock, 0, 5);
        await Tool("Bold").ClickAsync();
        await ShouldHaveNodesAsync(P(Strong(T("plain"))));

        await Start.FocusAsync();
        await UndoAsync();
        await ShouldHaveNodesAsync(P(T("plain")));

        await Start.FocusAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+Shift+z");
        await ShouldHaveNodesAsync(P(Strong(T("plain"))));
    }

    [Theory]
    [InlineData("Inline code")]
    [InlineData("Bulleted list")]
    [InlineData("Numbered list")]
    [InlineData("Quote")]
    [InlineData("h2")]
    public async Task EveryCommand_IsUndone_AndRedone_ByTheBrowser(string command)
    {
        await OpenEmptyAsync();
        await Page.Keyboard.TypeAsync("word here");
        await SelectAsync(FirstBlock, 0, 4);
        if (command == "h2")
        {
            await StyleSelect.SelectOptionAsync(command);
        }
        else
        {
            await Tool(command).ClickAsync();
        }

        var applied = await SavedAsync();
        applied.Select(Canonical).ShouldNotBe([Canonical(P(T("word here")))]);

        var steps = await StepUntilAsync("ControlOrMeta+z", [P(T("word here"))]);
        await StepUntilAsync("ControlOrMeta+Shift+z", applied, steps);
    }

    // Presses the key until the document holds the nodes. One command can be a few of the browser's steps, such as
    // one for each line it changes, but no more.
    private async Task<int> StepUntilAsync(string key, string[] nodes, int most = 3)
    {
        for (var step = 1; step <= most; step++)
        {
            await Start.FocusAsync();
            await Page.Keyboard.PressAsync(key);
            if ((await SavedAsync()).Select(Canonical).SequenceEqual(nodes.Select(Canonical)))
            {
                return step;
            }
        }

        ShouldBeNodes(await SavedAsync(), nodes);
        return most;
    }

    [Fact]
    public async Task UndoAffectingAnotherSurface_ReachesDocument()
    {
        await OpenFixtureAsync("everything", "write");
        var elsewhere = Surface("more-prose").Locator("p");
        await PlaceCaretAsync(elsewhere);
        await Page.Keyboard.TypeAsync(" extra");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > p").First);
        TextOf(Nodes(await SaveAsync(Page), "more-prose")[0]).ShouldEndWith(" extra");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > p").First);

        for (var i = 0; i < " extra".Length && (await elsewhere.TextContentAsync())!.Contains("extra"); i++)
        {
            await UndoAsync();
        }

        var saved = await SaveAsync(Page);
        TextOf(Nodes(saved, "more-prose")[0]).ShouldBe((await elsewhere.TextContentAsync())!);
        saved.ShouldBe(Original("everything"));
    }

    [Fact]
    public async Task SelectAllDelete_LeavesTypableEmptySurface_DocumentValid()
    {
        await OpenFixtureAsync("everything", "write");
        var surface = Surface("prose", 1);
        await surface.ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("Delete");
        await SaveAsync(Page);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();

        await Surface("prose", 0).ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("Delete");
        await SaveAsync(Page);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        await Surface("prose", 0).FocusAsync();
        await Page.Keyboard.TypeAsync("fresh start");

        var saved = await SaveAsync(Page);
        var nodes = Nodes(saved, "prose");
        TextOf(nodes[0]).ShouldBe("fresh start");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    // ---- Delivery and policy ----

    [Theory]
    [InlineData("bold")]
    [InlineData("paste")]
    [InlineData("undo")]
    [InlineData("indent")]
    [InlineData("link-click")]
    [InlineData("link-enter")]
    [InlineData("link-remove")]
    public async Task SaveClickedInTheSameTaskAsACommand_HasItsResult(string what)
    {
        await OpenEmptyAsync();
        await PasteAsync("first\n\nsecond");
        if (what == "indent")
        {
            await SelectAsync(Start, 0, 11);
            await Tool("Bulleted list").ClickAsync();
            await Expect(Start.Locator("li")).ToHaveCountAsync(2);
            await PlaceCaretAsync(Start.Locator("li").Nth(1));
        }
        else
        {
            await SelectAsync(FirstBlock, 0, 5);
            if (what == "undo")
            {
                await Tool("Italic").ClickAsync();
                await Expect(Start.Locator("i, em")).ToHaveCountAsync(1);
            }
        }

        if (what.StartsWith("link", StringComparison.Ordinal))
        {
            if (what == "link-remove")
            {
                await Tool("Link").ClickAsync();
                await ApplyLinkAsync("/old");
                await Expect(Start.Locator("a")).ToHaveCountAsync(1);
                await PlaceCaretAsync(FirstBlock, 2);
                await Expect(Tool("Link")).ToHaveAttributeAsync("aria-pressed", "true");
            }

            await Tool("Link").ClickAsync();
            if (what != "link-remove")
            {
                await LinkAddress.FillAsync("  /same-task  ");
                // .NET has answered for what is typed.
                await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" }))
                    .ToHaveAttributeAsync("data-rt-address", "/same-task");
            }
        }

        await SaveAsync(Page);
        var save = await SaveNumberAsync(Page);

        await Page.EvaluateAsync("""
            what => {
                const surface = document.querySelector('[data-rt-surface]');
                if (what === 'bold') {
                    document.querySelector('[data-rt-command="bold"]').click();
                } else if (what === 'paste') {
                    const event = new Event('paste', { bubbles: true, cancelable: true });
                    Object.defineProperty(event, 'clipboardData', { value: { getData: () => 'PASTED\n\nTWICE' } });
                    surface.dispatchEvent(event);
                } else if (what === 'link-click') {
                    document.querySelector('[data-rt-link-apply]').click();
                } else if (what === 'link-enter') {
                    document.querySelector('.rt-link-box input')
                        .dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
                } else if (what === 'link-remove') {
                    document.querySelector('[data-rt-link-remove]').click();
                } else if (what === 'undo') {
                    document.execCommand('undo');
                } else {
                    surface.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
                }
                document.getElementById('save').click();
            }
            """, what);
        await WaitForSaveAsync(Page, save);

        var nodes = CompactAll(Nodes((await Page.Locator("#saved").TextContentAsync())!, "start"));
        var shown = Canonical((await SavedAsync())[0]);
        Canonical(nodes[0]).ShouldBe(shown);
        if (what.StartsWith("link", StringComparison.Ordinal))
        {
            Canonical(nodes[0]).ShouldBe(Canonical(what == "link-remove"
                ? P(T("first"))
                : P(Link("/same-task", T("first")))));
            // The box has closed, and the command ran once.
            await Expect(LinkAddress).ToHaveCountAsync(0);
            (await SavedAsync()).Select(Canonical).ShouldBe(nodes.Select(Canonical));
        }
        else if (what != "undo")
        {
            shown.ShouldNotBe(Canonical(P(T("first"))));
        }
        else
        {
            shown.ShouldBe(Canonical(P(T("first"))));
        }
    }

    [Fact]
    public async Task StrictCsp_CommandsLinkAndPasteWork()
    {
        await OpenEmptyAsync(strictCsp: true);
        await Page.Keyboard.TypeAsync("one two three four");
        await SelectAsync(FirstBlock, 0, 3);
        await Tool("Bold").ClickAsync();
        await SelectAsync(FirstBlock, 4, 7);
        await Tool("Italic").ClickAsync();
        await SelectAsync(FirstBlock, 8, 13);
        await Tool("Inline code").ClickAsync();
        await SelectAsync(FirstBlock, 14, 18);
        await Tool("Link").ClickAsync();
        await ApplyLinkAsync("/csp");
        await Expect(Start.Locator("a")).ToHaveCountAsync(1);
        await PlaceCaretAsync(FirstBlock);
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.PressAsync("Enter");
        await PasteAsync("pasted\n\nmore", "<p style=\"color:red\">no</p>");
        await StyleSelect.SelectOptionAsync("h3");
        await StyleSelect.SelectOptionAsync("p");
        await Tool("Bulleted list").ClickAsync();
        await Expect(Start.Locator("li")).ToHaveCountAsync(1);
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("nested");
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("quoted");
        await Tool("Quote").ClickAsync();
        await Expect(Tool("Quote")).ToHaveAttributeAsync("aria-pressed", "true");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("end");

        var saved = await SavedAsync();
        ShouldBeNodes(
            saved,
            P(Strong(T("one")), T(" "), Em(T("two")), T(" "), Code("three"), T(" "), Link("/csp", T("four"))),
            P(T("pasted")),
            Ul(Li(T("more"), Ul(Li(T("nested"))))),
            Quote(P(T("quoted"))),
            P(T("end")));
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        // WebKit makes and removes a styled element of its own while some commands run; none may be left.
        await Expect(Start.Locator("[style]")).ToHaveCountAsync(0);
        // The harness fails the test if the policy was violated. It allows style attributes, as the sample does.
    }
}
