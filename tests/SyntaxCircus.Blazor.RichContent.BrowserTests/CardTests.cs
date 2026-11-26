using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>Cards, empty places and Insert in Write mode.</summary>
public class CardTests(SampleHost host) : CardTest(host)
{
    // In the "everything" fixture's first block: the first code block, and the paragraph between it and the second.
    private const int CodeNode = 12;
    private const int BetweenCards = 13;
    private const int ImageNode = 15;
    private const int TableNode = 17;

    // Records every message a code card sends, and passes it on.
    private const string TapCard = """
        element => {
            window.chunks = [];
            const transport = element.richContentCode.transport;
            const real = transport.invoke;
            transport.invoke = (sequence, index, count, chunk) => {
                window.chunks.push({ index, count, length: chunk.length });
                return real(sequence, index, count, chunk);
            };
        }
        """;

    [Fact]
    public async Task CodeCard_40KbEdit_OnDefaultHubOptions_KeepsCircuit_AndSavesIt()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        await area.EvaluateAsync(TapCard);
        var text = LargeCode();
        text.Length.ShouldBeGreaterThan(40_000);

        await area.FocusAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.InsertTextAsync(text);
        var saved = await SaveAsync(Page);

        ((string)Nodes(saved, "prose")[CodeNode]!["text"]!).ShouldBe(text);
        AssertOnlyNodesReplaced(Original("everything"), saved, "prose", CodeNode, 1, 1);
        var lengths = await Page.EvaluateAsync<int[]>("() => window.chunks.map(c => c.length)");
        lengths.Length.ShouldBeGreaterThan(10);
        lengths.ShouldAllBe(length => length <= 4000);

        // The circuit is the same one, and goes on taking edits.
        await area.FocusAsync();
        await area.PressAsync("ControlOrMeta+End");
        await Page.Keyboard.TypeAsync("!");
        ((string)Nodes(await SaveAsync(Page), "prose")[CodeNode]!["text"]!).Length.ShouldBe(text.Length + 1);
    }

    // Over 40,000 characters, with what makes messages larger than their text: quotes, backslashes, characters
    // outside the basic plane (which must not be split) and others of several bytes.
    private static string LargeCode()
    {
        var text = new StringBuilder();
        for (var line = 0; text.Length <= 40_000; line++)
        {
            text.Append("var s").Append(line).Append(" = \"\\\\ <tag> é 漢 \U0001F600\U0001F680\U0001F9EA\";\n");
        }

        return text.ToString();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeCard_TabAndEscapeTab(bool strictCsp)
    {
        await OpenFixtureAsync("empty", "write", strictCsp);
        await Start.ClickAsync();
        await InsertAsync("Code block");
        var area = await CodeAsync("start");
        // The inserted card takes the focus.
        await Expect(area).ToBeFocusedAsync();

        await Page.Keyboard.TypeAsync("a");
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.TypeAsync("b");

        await Expect(area).ToBeFocusedAsync();
        await Expect(area).ToHaveValueAsync("a    b");
        ((string)Nodes(await SaveAsync(Page), "start")[0]!["text"]!).ShouldBe("a    b");

        await area.FocusAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Page.Keyboard.PressAsync("Tab");

        await Expect(area).Not.ToBeFocusedAsync();
        await Expect(area).ToHaveValueAsync("a    b");

        // Escape only lets the next Tab go. Back in the field, Tab indents again.
        await area.FocusAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Page.Keyboard.TypeAsync("c");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(area).ToBeFocusedAsync();
        (await area.InputValueAsync()).ShouldContain("c    ");

        await Page.Keyboard.PressAsync("Escape");
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Expect(area).Not.ToBeFocusedAsync();
    }

    [Fact]
    public async Task CodeCard_TheIndent_IsUndoneByTheBrowser_AndTheUndoIsDelivered()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await InsertAsync("Code block");
        var area = await CodeAsync("start");
        await Expect(area).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Tab");
        await Expect(area).ToHaveValueAsync("    ");
        await Page.Keyboard.PressAsync("ControlOrMeta+z");

        await Expect(area).ToHaveValueAsync("");
        ((string)Nodes(await SaveAsync(Page), "start")[0]!["text"]!).ShouldBe("");
    }

    [Fact]
    public async Task CodeCard_SaveClickedInTheSameTaskAsAnEdit_HasIt()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        var before = await SaveNumberAsync(Page);

        await area.EvaluateAsync(
            "element => { element.value = 'typed then saved'; element.dispatchEvent(new InputEvent('input', { bubbles: true })); document.getElementById('save').click(); }");

        await WaitForSaveAsync(Page, before);
        var saved = await Page.Locator("#saved").TextContentAsync();
        ((string)Nodes(saved!, "prose")[CodeNode]!["text"]!).ShouldBe("typed then saved");
    }

    [Fact]
    public async Task CodeCard_SendsNothingDuringAComposition_AndTheResultWhenItEnds()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        await area.EvaluateAsync(TapCard);

        await area.EvaluateAsync("""
            element => {
                element.dispatchEvent(new CompositionEvent('compositionstart'));
                element.value = 'compos';
                element.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true }));
            }
            """);
        await SaveAsync(Page);
        (await Page.EvaluateAsync<int>("() => window.chunks.length")).ShouldBe(0);

        await area.EvaluateAsync("""
            element => {
                element.value = 'composed';
                element.dispatchEvent(new CompositionEvent('compositionend'));
            }
            """);

        ((string)Nodes(await SaveAsync(Page), "prose")[CodeNode]!["text"]!).ShouldBe("composed");
        (await Page.EvaluateAsync<int>("() => window.chunks.length")).ShouldBe(1);
    }

    [Fact]
    public async Task CodeCard_ChoosingALanguage_KeepsTheTextTypedJustBefore()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        await area.FillAsync("select 1;");

        await Card("prose", "code").First.GetByLabel("Language").SelectOptionAsync("sql");
        await area.FocusAsync();
        await area.PressAsync("ControlOrMeta+End");
        await Page.Keyboard.TypeAsync("!");
        var node = Nodes(await SaveAsync(Page), "prose")[CodeNode]!;

        ((string)node["text"]!).ShouldBe("select 1;!");
        ((string)node["language"]!).ShouldBe("sql");
        await Expect(area).ToHaveValueAsync("select 1;!");
    }

    [Fact]
    public async Task CodeCard_OpeningCodeWithCarriageReturns_RaisesNoChange_AndAReplacementIsShown()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");

        // A host that reloads the same stored document, in new objects.
        await Page.Locator("#replace-same").ClickAsync();
        await SaveAsync(Page);

        (await ChangeCountAsync(Page)).ShouldBe(0);
        (await area.EvaluateAsync<int>("element => element.richContentCode.generation")).ShouldBe(1, "equal text is not set again");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeCard_AndTableCell_TypedWhileDisconnected_ArriveAfterReconnecting(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        var area = await CodeAsync("prose");
        var cell = Card("prose", "table").Locator("[data-rt-surface]").First;
        await area.FillAsync("one");
        await SaveAsync(Page);

        var outage = await Watch.DropConnectionAsync(Page);
        await area.EvaluateAsync("""
            element => {
                for (const character of ' offline') {
                    element.value += character;
                    element.dispatchEvent(new InputEvent('input', { bubbles: true }));
                }
            }
            """);
        await cell.EvaluateAsync("""
            element => {
                element.firstChild.appendData(' offline');
                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();

        await Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('[data-rt-code], [data-rt-surface]')].every(e => (e.richContentCode ?? e.richContentSurface).transport.unacknowledged === 0)",
            null,
            new() { Timeout = 30_000 });
        var nodes = Nodes(await SaveAsync(Page), "prose");
        ((string)nodes[CodeNode]!["text"]!).ShouldBe("one offline");
        TextOf(nodes[TableNode]!["children"]![0]!["children"]![0]).ShouldBe("Name offline");
    }

    [Fact]
    public async Task MovingACodeCard_KeepsItsElementAndText_AndItStillDelivers()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        await area.FillAsync("moved");
        await area.EvaluateAsync("element => { element.marked = true; }");

        await Card("prose", "code").First.GetByRole(AriaRole.Button, new() { Name = "Move up" }).ClickAsync();
        await WaitForChangesAsync(2);

        (await area.EvaluateAsync<bool>("element => element.marked === true")).ShouldBeTrue();
        await Expect(area).ToHaveValueAsync("moved");
        await area.FocusAsync();
        await area.PressAsync("ControlOrMeta+End");
        await Page.Keyboard.TypeAsync("!");
        var nodes = Nodes(await SaveAsync(Page), "prose");
        ((string)nodes[CodeNode - 1]!["text"]!).ShouldBe("moved!");
        // The quote it passed is as it was.
        Compact(nodes[CodeNode]).ShouldBe(Compact(Nodes(Original("everything"), "prose")[CodeNode - 1]));
        await Expect(Page.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RemovingACard_AsksInPlace_AndTheRestGoesOnWorking_ThroughAnOutage()
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        var card = Card("prose", "code").First;

        await card.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();

        var no = card.GetByRole(AriaRole.Button, new() { Name = "No", Exact = true });
        await Expect(no).ToBeFocusedAsync();
        (await ChangeCountAsync(Page)).ShouldBe(0);
        await card.GetByRole(AriaRole.Button, new() { Name = "Yes", Exact = true }).ClickAsync();
        await WaitForChangesAsync(1);

        // Disposing the card and the merged surfaces must not have harmed the circuit, which an outage and the
        // test's leaving the page would show.
        var outage = await Watch.DropConnectionAsync(Page);
        await outage.RestoreAsync();
        var merged = Surface("prose");
        await Expect(merged).ToContainTextAsync("Writing between two cards.");
        await PlaceCaretAsync(merged.Locator(":scope > p").Last);
        await Page.Keyboard.TypeAsync(" Still here.");
        await WaitUntilAcknowledgedAsync();

        var nodes = Nodes(await SaveAsync(Page), "prose");
        nodes.Count.ShouldBe(Nodes(Original("everything"), "prose").Count - 1);
        TextOf(nodes[CodeNode]).ShouldBe("Writing between two cards. Still here.");
        var expected = CompactAll(Nodes(Original("everything"), "prose")).ToList();
        expected.RemoveAt(CodeNode);
        CompactAll(nodes).Where((_, index) => index != CodeNode).ShouldBe(expected.Where((_, index) => index != CodeNode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageCard_PickAltCaption_PreviewShown(bool strictCsp)
    {
        await OpenFixtureAsync("empty", "write", strictCsp);
        await Start.ClickAsync();
        await InsertAsync("Image");
        var card = Card("start", "image");
        await Expect(card.GetByLabel("Alt text")).ToBeFocusedAsync();
        await Expect(card.Locator("img")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#errors li")).ToContainTextAsync(["media-required", "alt-required"]);

        await card.GetByLabel("Choose image").SelectOptionAsync("harbour");
        await card.GetByLabel("Alt text").FillAsync("A harbour");
        await card.GetByLabel("Caption").FillAsync("At dawn");

        var preview = card.Locator("img");
        await Expect(preview).ToHaveAttributeAsync("src", "/img/harbour.svg");
        await Expect(preview).ToHaveAttributeAsync("alt", "A harbour");
        await Page.WaitForFunctionAsync("() => document.querySelector('[data-rt-card=image] img').naturalWidth > 0");
        ShouldBeNodes(
            CompactAll(Nodes(await SaveAsync(Page), "start")),
            """{"kind":"image","media":{"id":"harbour"},"alt":"A harbour","caption":"At dawn"}""");
        await Expect(Page.Locator("#errors li")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Table_EditCell_AddRowAndColumn(bool strictCsp)
    {
        await OpenFixtureAsync("empty", "write", strictCsp);
        await Start.ClickAsync();
        await InsertAsync("Table");
        var card = Card("start", "table");
        var cells = card.Locator("[data-rt-surface]");
        await Expect(cells).ToHaveCountAsync(4);
        await Expect(cells.First).ToBeFocusedAsync();
        await Expect(Tool("Insert")).ToBeDisabledAsync();
        await Expect(Toolbar.GetByLabel("Text style")).ToBeDisabledAsync();

        await Page.Keyboard.TypeAsync("Name");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Shift+Enter");
        await Page.Keyboard.TypeAsync("s");
        await Expect(cells.First).ToHaveTextAsync("Names");

        // Tab moves to the next cell, and Shift+Tab back.
        await Page.Keyboard.PressAsync("Tab");
        await Expect(cells.Nth(1)).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+b");
        await Page.Keyboard.TypeAsync("Value");
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Expect(cells.First).ToBeFocusedAsync();

        await PlaceCaretAsync(cells.Nth(2));
        // Not a real ClipboardEvent, whose data Firefox leaves empty when a script makes one.
        await cells.Nth(2).EvaluateAsync("""
            element => {
                const event = new Event('paste', { bubbles: true, cancelable: true });
                Object.defineProperty(event, 'clipboardData', { value: { getData: () => 'two\nlines\n\nand more' } });
                element.dispatchEvent(event);
            }
            """);
        await Expect(cells.Nth(2)).ToHaveTextAsync("two lines and more");
        await cells.First.EvaluateAsync("element => { element.marked = true; }");

        await card.GetByRole(AriaRole.Button, new() { Name = "Add row" }).ClickAsync();
        await Expect(cells).ToHaveCountAsync(6);
        await card.GetByRole(AriaRole.Button, new() { Name = "Add column" }).ClickAsync();
        await Expect(cells).ToHaveCountAsync(9);
        await Expect(card.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
        (await cells.First.EvaluateAsync<bool>("element => element.marked === true")).ShouldBeTrue("a cell keeps its surface");
        await cells.Nth(8).ClickAsync();
        await Page.Keyboard.TypeAsync("last");
        await WaitUntilAcknowledgedAsync();

        var table = Nodes(await SaveAsync(Page), "start").ShouldHaveSingleItem()!;
        var rows = table["children"]!.AsArray();
        rows.Select(row => string.Join(',', row!["children"]!.AsArray().Select(TextOf)))
            .ShouldBe(["Names,Value,", "two lines and more,,", ",,last"]);
        rows.Select(row => string.Join(',', row!["children"]!.AsArray().Select(cell => (bool?)cell!["header"])))
            .ShouldBe(["True,True,True", "False,False,False", "False,False,False"]);
        ShouldBeNodes(
            [Compact(rows[0]!["children"]![1]!["children"]!.AsArray().Single())],
            """{"kind":"strong","children":[{"kind":"text","text":"Value"}]}""");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();

        // The first row stops being a header, and only it changes.
        await card.GetByLabel("First row is a header").UncheckAsync();
        await WaitForHeaderAsync(card, 0);
        var unheaded = Nodes(await SaveAsync(Page), "start")[0]!["children"]!.AsArray();
        unheaded[0]!["children"]!.AsArray().Select(cell => (bool?)cell!["header"]).ShouldBe([false, false, false]);
        CompactAll(unheaded.Skip(1)).ShouldBe(CompactAll(rows.Skip(1)));

        // A row with text in it is asked about; the column that then holds none is not.
        await card.GetByRole(AriaRole.Button, new() { Name = "Remove last row" }).ClickAsync();
        await card.GetByRole(AriaRole.Button, new() { Name = "Yes", Exact = true }).ClickAsync();
        await Expect(cells).ToHaveCountAsync(6);
        await card.GetByRole(AriaRole.Button, new() { Name = "Remove last column" }).ClickAsync();
        await Expect(cells).ToHaveCountAsync(4);
        (await cells.First.EvaluateAsync<bool>("element => element.marked === true")).ShouldBeTrue("a cell keeps its surface");
    }

    private static Task WaitForHeaderAsync(ILocator card, int headers) =>
        Expect(card.Locator("td[data-rt-header]")).ToHaveCountAsync(headers);

    [Fact]
    public async Task Table_EmptyingACell_AndTypingAgain_KeepsTheSameDrawing_AndStaysValid()
    {
        await OpenFixtureAsync("everything", "write");
        var cell = Card("prose", "table").Locator("[data-rt-surface]").Nth(2);
        await PlaceCaretAsync(cell);
        for (var i = 0; i < "Bold cell".Length; i++)
        {
            await Page.Keyboard.PressAsync("Backspace");
        }

        await WaitUntilAcknowledgedAsync();
        await SaveAsync(Page);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        await PlaceCaretAsync(cell);
        await Page.Keyboard.TypeAsync("new");
        var saved = await SaveAsync(Page);

        (await cell.EvaluateAsync<int>("element => element.richContentSurface.generation")).ShouldBe(1);
        TextOf(Nodes(saved, "prose")[TableNode]!["children"]![1]!["children"]![0]).ShouldBe("new");
        AssertOnlyNodesReplaced(Original("everything"), saved, "prose", TableNode, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_SplitsSurface_KeepsAllNodes(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        var heading = Surface("prose").Locator(":scope > h2");
        await PlaceCaretAsync(heading, 3);
        // Typed immediately before: the split must have it.
        await Page.Keyboard.TypeAsync("XY");

        await InsertAsync("Code block");

        var area = Page.Locator("[data-block-id='prose'] textarea[data-rt-code]").First;
        await Expect(area).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("inserted");
        var saved = await SaveAsync(Page);
        var old = Nodes(Original("everything"), "prose");
        var now = Nodes(saved, "prose");
        now.Count.ShouldBe(old.Count + 1);
        TextOf(now[1]).ShouldBe("A hXYeading");
        ShouldBeNodes([Compact(now[2])], """{"kind":"codeBlock","text":"inserted"}""");
        CompactAll(now.Where((_, index) => index is not (1 or 2))).ShouldBe(CompactAll(old.Where((_, index) => index != 1)));

        // Both pieces of the split surface go on delivering.
        await Expect(Page.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await Page.Keyboard.TypeAsync("1");
        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > h3"), 0);
        await Page.Keyboard.TypeAsync("2");
        await WaitUntilAcknowledgedAsync();
        now = Nodes(await SaveAsync(Page), "prose");
        TextOf(now[1]).ShouldBe("A hXYeading1");
        TextOf(now[3]).ShouldStartWith("2A subheading");
        now.Count.ShouldBe(old.Count + 1);
    }

    [Fact]
    public async Task Insert_InAList_GoesAfterTheWholeList_AndABlockSplitsTheTextBlock()
    {
        await OpenFixtureAsync("everything", "write");
        var old = Nodes(Original("everything"), "prose");
        var list = Surface("prose").Locator(":scope > ul").First;
        await PlaceCaretAsync(list.Locator("li").First, 2);

        await InsertAsync("Call to action");

        await Expect(Page.Locator("[data-block-kind=cta]")).ToHaveCountAsync(2);
        var saved = JsonNode.Parse(await SaveAsync(Page))!["blocks"]!.AsArray();
        saved.Select(block => (string)block!["$block"]!).Take(4).ShouldBe(["rich-text", "cta", "rich-text", "rich-text"]);
        var listIndex = old.Select((node, index) => (node, index)).First(entry => (string)entry.node!["kind"]! == "list").index;
        CompactAll(saved[0]!["nodes"]!.AsArray()).ShouldBe(CompactAll(old.Take(listIndex + 1)));
        CompactAll(saved[2]!["nodes"]!.AsArray()).ShouldBe(CompactAll(old.Skip(listIndex + 1)));
        ((string)saved[0]!["id"]!).ShouldBe("prose");
        await Expect(Page.Locator("[data-rt-surface]:not([data-rt-ready])")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Insert_FromTheKeyboard_GoesAfterTheParagraphWithTheCursor()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await Page.Keyboard.TypeAsync("one");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("two");
        await PlaceCaretAsync(Start.Locator("p").First, 1);
        await Expect(Tool("Insert")).ToBeEnabledAsync();

        await Page.Keyboard.PressAsync("Alt+F10");
        await Tool("Insert").FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        var choice = Page.GetByRole(AriaRole.Group, new() { Name = "Insert", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Code block" });
        await Expect(choice).ToBeVisibleAsync();
        await choice.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Expect(await CodeAsync("start")).ToBeFocusedAsync();
        Nodes(await SaveAsync(Page), "start").Select(node => (string)node!["kind"]!)
            .ShouldBe(["paragraph", "codeBlock", "paragraph"]);
    }

    [Theory]
    // A code card's text, a card's input, a card that only shows, a block's header, and a field outside the editor.
    [InlineData("textarea[data-rt-code]")]
    [InlineData("[data-rt-card=image] input[type=text]")]
    [InlineData("[data-rt-card=code] strong")]
    [InlineData("[data-block-id=gallery] > header strong")]
    [InlineData("#outside")]
    public async Task TheToolbar_LetsGoOfASurface_WhenTheAuthorGoesElsewhere(string elsewhere)
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        await Page.EvaluateAsync(
            "() => { const field = document.body.insertBefore(document.createElement('input'), document.body.firstChild); field.id = 'outside'; }");
        var paragraph = Surface("prose", 1).Locator(":scope > p");
        await PlaceCaretAsync(paragraph, 3);
        await Expect(Tool("Bold")).ToBeEnabledAsync();
        await Expect(Tool("Insert")).ToBeEnabledAsync();

        await Page.Locator(elsewhere).First.ClickAsync();

        await Expect(Tool("Bold")).ToBeDisabledAsync();
        await Expect(Tool("Insert")).ToBeDisabledAsync();
        (await ChangeCountAsync(Page)).ShouldBe(0);

        await paragraph.ClickAsync();
        await Expect(Tool("Insert")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task TheToolbar_ComesBack_WhenTheFocusReturnsWithoutTheSelectionMoving()
    {
        await OpenFixtureAsync("everything", "write");
        var surface = Surface("prose", 1);
        await PlaceCaretAsync(surface.Locator(":scope > p"), 3);
        await Expect(Tool("Bold")).ToBeEnabledAsync();
        // As some browsers do for a button: the focus moves and the selection stays.
        await Card("prose", "image").First.GetByRole(AriaRole.Button, new() { Name = "Move up" }).FocusAsync();
        await Expect(Tool("Bold")).ToBeDisabledAsync();

        await surface.FocusAsync();

        await Expect(Tool("Bold")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task TheToolbar_StillActsWhereTheAuthorWasWriting_AfterAButtonOutsideTheEditor()
    {
        await OpenFixtureAsync("empty", "write");
        await Start.ClickAsync();
        await Page.Keyboard.TypeAsync("word");
        await Page.Keyboard.PressAsync("Shift+Home");

        await SaveAsync(Page);

        await Expect(Tool("Bold")).ToBeEnabledAsync();
        await Tool("Bold").ClickAsync();
        await Expect(Start).ToBeFocusedAsync();
        ShouldBeNodes(
            CompactAll(Nodes(await SaveAsync(Page), "start")),
            """{"kind":"paragraph","children":[{"kind":"strong","children":[{"kind":"text","text":"word"}]}]}""");
    }

    [Fact]
    public async Task TypingInEmptyPlace_KeepsFocusAndCaret_AndStores()
    {
        await OpenFixtureAsync("everything", "write");
        // Between the two images that follow one another.
        var image = Card("prose", "image").First;
        var place = image.Locator("xpath=following-sibling::*[1][@data-rt-surface]");
        await Expect(place).ToHaveTextAsync("");
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        await place.EvaluateAsync("element => { element.marked = true; }");
        await place.ClickAsync();

        await Page.Keyboard.TypeAsync("be");
        await WaitForChangesAsync(1);
        await SaveAsync(Page);
        // Save took the focus. Back where the cursor was, without moving it.
        await place.FocusAsync();
        await PlaceCaretAsync(place.Locator("p"));
        await Page.Keyboard.TypeAsync("tween");
        await Page.Keyboard.PressAsync("ArrowLeft");
        await Page.Keyboard.TypeAsync("!");
        await Expect(place).ToBeFocusedAsync();
        (await place.EvaluateAsync<bool>("element => element.marked === true")).ShouldBeTrue();

        var saved = await SaveAsync(Page);
        var added = AssertOnlyNodesReplaced(Original("everything"), saved, "prose", ImageNode + 1, 0, 1);
        TextOf(added[0]).ShouldBe("betwee!n");
        (await place.EvaluateAsync<int>("element => element.richContentSurface.generation")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BetweenTwoCallouts_TypingCreatesRichTextBlock(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        var place = Page.Locator("[data-block-id='callout-tip'] ~ section[data-rt-place]").First;
        var surface = place.Locator("[data-rt-surface]");
        await Expect(surface).ToHaveAttributeAsync("data-rt-ready", "true");
        await surface.EvaluateAsync("element => { element.marked = true; }");
        var marked = Page.Locator("[data-rt-surface]").Filter(new() { HasText = "Between the callouts." });
        await surface.ClickAsync();

        await Page.Keyboard.TypeAsync("Between");
        // The place has become a block, and the same element goes on being typed in.
        await Expect(Page.Locator("[data-block-id='callout-tip'] ~ section[data-block-kind=rich-text]")).ToHaveCountAsync(1);
        await Page.Keyboard.TypeAsync(" the callouts.");

        await Expect(marked).ToBeFocusedAsync();
        (await marked.EvaluateAsync<bool>("element => element.marked === true")).ShouldBeTrue();
        var old = JsonNode.Parse(Original("everything"))!["blocks"]!.AsArray();
        var now = JsonNode.Parse(await SaveAsync(Page))!["blocks"]!.AsArray();
        now.Count.ShouldBe(old.Count + 1);
        var at = old.Select((block, index) => (block, index)).Single(entry => (string)entry.block!["id"]! == "callout-tip").index + 1;
        ((string)now[at]!["$block"]!).ShouldBe("rich-text");
        ShouldBeNodes(
            CompactAll(now[at]!["nodes"]!.AsArray()),
            """{"kind":"paragraph","children":[{"kind":"text","text":"Between the callouts."}]}""");
        CompactAll(now.Where((_, index) => index != at)).ShouldBe(CompactAll(old));
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InsertInAnEmptyPlaceBetweenBlocks_MakesABlockThere()
    {
        await OpenFixtureAsync("everything", "write");
        var surface = Page.Locator("[data-block-id='callout-tip'] ~ section[data-rt-place]").First.Locator("[data-rt-surface]");
        await surface.ClickAsync();

        await InsertAsync("Code block");

        var area = Page.Locator("[data-block-id='callout-tip'] ~ section[data-block-kind=rich-text] textarea[data-rt-code]");
        await Expect(area).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("x");
        var now = JsonNode.Parse(await SaveAsync(Page))!["blocks"]!.AsArray();
        var at = now.Select((block, index) => (block, index)).Single(entry => (string)entry.block!["id"]! == "callout-tip").index + 1;
        ShouldBeNodes(CompactAll(now[at]!["nodes"]!.AsArray()), """{"kind":"codeBlock","text":"x"}""");
    }

    [Fact]
    public async Task ACalloutBody_HasSurfacesAndNestedCards_ThatDeliverIntoTheCallout()
    {
        await OpenFixtureAsync("everything", "write");
        var callout = Page.Locator("[data-block-id='callout-tip']");
        var area = await CodeAsync("callout-tip");
        await area.FillAsync("dotnet test");
        await PlaceCaretAsync(callout.Locator("[data-rt-surface] > p").First);
        await Page.Keyboard.TypeAsync(" More.");
        await callout.GetByLabel("Title", new() { Exact = true }).FillAsync("A new title");
        await PlaceCaretAsync(callout.Locator("[data-rt-card=table] [data-rt-surface]"));
        await Page.Keyboard.TypeAsync("!");
        await WaitUntilAcknowledgedAsync();

        var saved = JsonNode.Parse(await SaveAsync(Page))!["blocks"]!.AsArray().Single(block => (string)block!["id"]! == "callout-tip")!;

        ((string)saved["title"]!).ShouldBe("A new title");
        var nodes = saved["nodes"]!.AsArray();
        TextOf(nodes[0]).ShouldBe("Callout text with emphasis. More.");
        ((string)nodes[1]!["text"]!).ShouldBe("dotnet test");
        TextOf(nodes[3]!["children"]![0]!["children"]![0]).ShouldBe("In a callout!");
    }

    [Fact]
    public async Task BackspaceAtStartAfterCard_DoesNotDeleteCard()
    {
        await OpenFixtureAsync("everything", "write");
        var cards = Page.Locator("[data-block-id='prose'] [data-rt-card]");
        var count = await cards.CountAsync();
        var after = Surface("prose", 1).Locator(":scope > p");

        await PlaceCaretAsync(after, 0);
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.PressAsync("ControlOrMeta+Backspace");
        // Delete at the end of the surface before a card.
        await PlaceCaretAsync(after);
        await Page.Keyboard.PressAsync("Delete");
        await Page.Keyboard.PressAsync("ControlOrMeta+Delete");
        // In the empty places too, which have nothing else to delete.
        var place = Card("prose", "image").First.Locator("xpath=following-sibling::*[1][@data-rt-surface]");
        await place.ClickAsync();
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.PressAsync("Delete");

        await Expect(cards).ToHaveCountAsync(count);
        (await SaveAsync(Page)).ShouldBe(Original("everything"));

        // Selecting all selects within the surface.
        await PlaceCaretAsync(after);
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("Delete");
        await WaitUntilAcknowledgedAsync();
        await Expect(cards).ToHaveCountAsync(count);
        var saved = await SaveAsync(Page);
        CompactAll(Nodes(saved, "prose").Where(node => (string)node!["kind"]! is "codeBlock" or "image" or "table"))
            .ShouldBe(CompactAll(Nodes(Original("everything"), "prose").Where(node => (string)node!["kind"]! is "codeBlock" or "image" or "table")));
    }

    [Fact]
    public async Task LockedNodes_StayByteIdentical_AfterEditingNeighbours()
    {
        await OpenFixtureAsync("unknown", "write");
        await Expect(Card("mixed", "locked")).ToHaveCountAsync(6);
        (await SaveAsync(Page)).ShouldBe(Original("unknown"));
        var surfaces = Page.Locator("[data-block-id='mixed'] [data-rt-surface]");

        await PlaceCaretAsync(surfaces.First.Locator("p"));
        await Page.Keyboard.TypeAsync(" Edited.");
        await PlaceCaretAsync(surfaces.Last.Locator("p"));
        await Page.Keyboard.TypeAsync(" Edited too.");
        // In an empty place between two locked cards.
        await Card("mixed", "locked").First.Locator("xpath=following-sibling::*[1][@data-rt-surface]").ClickAsync();
        await Page.Keyboard.TypeAsync("New.");
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        var old = CompactAll(Nodes(Original("unknown"), "mixed"));
        var now = CompactAll(Nodes(saved, "mixed"));
        now.Length.ShouldBe(old.Length + 1);
        string[] untouched = [old[1], old[2], old[3], old[4], old[5], old[6], old[7]];
        new[] { now[1], now[3], now[4], now[5], now[6], now[7], now[8] }.ShouldBe(untouched);
        TextOf(JsonNode.Parse(now[2])).ShouldBe("New.");

        // Moving a locked card past its neighbour keeps both as they were, and so does every other block.
        await Card("mixed", "locked").First.GetByRole(AriaRole.Button, new() { Name = "Move down" }).ClickAsync();
        await WaitForChangesAsync(4);
        var moved = await SaveAsync(Page);
        CompactAll(Nodes(moved, "mixed")).Order().ShouldBe(CompactAll(Nodes(saved, "mixed")).Order());
        CompactAll(Nodes(moved, "mixed"))[2].ShouldBe(now[1]);
        var otherBlocks = (string json) => CompactAll(JsonNode.Parse(json)!["blocks"]!.AsArray().Skip(1));
        otherBlocks(moved).ShouldBe(otherBlocks(Original("unknown")));
        Compact(JsonNode.Parse(moved)!["blocks"]![0]!["futureBlockSetting"]).ShouldBe("\"keep me\"");
    }

    [Fact]
    public async Task EditingACodeCard_AnImageAndACell_ChangesOnlyThoseNodes()
    {
        await OpenFixtureAsync("everything", "write");
        var area = await CodeAsync("prose");
        await area.FillAsync("changed");
        await Card("prose", "image").First.GetByLabel("Alt text").FillAsync("Changed alt");
        var cell = Card("prose", "table").Locator("[data-rt-surface]").First;
        await PlaceCaretAsync(cell);
        await Page.Keyboard.TypeAsync("s");
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        var old = CompactAll(Nodes(Original("everything"), "prose"));
        var now = CompactAll(Nodes(saved, "prose"));
        Enumerable.Range(0, old.Length).Where(index => old[index] != now[index]).ShouldBe([CodeNode, ImageNode, TableNode]);
    }

    [Fact]
    public async Task ADisabledEditor_HasReadOnlyCards()
    {
        await Page.GotoAsync(new Uri(Host.Address(), "fixture/everything?mode=write&disabled=true").AbsoluteUri);
        await WaitForSurfacesAsync(Page);
        var area = await CodeAsync("prose");

        await Expect(area).ToHaveAttributeAsync("readonly", new Regex("^$|readonly|true"));
        (await area.EvaluateAsync<bool>("element => element.readOnly")).ShouldBeTrue();
        await area.FocusAsync();
        await Page.Keyboard.TypeAsync("no");
        await Page.Keyboard.PressAsync("Tab");
        // Tab is not taken where nothing can be typed.
        await Expect(area).Not.ToBeFocusedAsync();
        await Expect(area).ToHaveValueAsync((string)Nodes(Original("everything"), "prose")[CodeNode]!["text"]!);
        var controls = Page.Locator("[data-rt-card] button, [data-rt-card] input, [data-rt-card] select, [role=toolbar] button");
        (await controls.EvaluateAllAsync<bool>("controls => controls.length > 10 && controls.every(c => c.disabled)")).ShouldBeTrue();
        foreach (var cell in await Page.Locator("[data-rt-card=table] [data-rt-surface]").AllAsync())
        {
            await Expect(cell).ToHaveAttributeAsync("contenteditable", "false");
        }

        (await ChangeCountAsync(Page)).ShouldBe(0);
    }
}

/// <summary>Cutting uses the machine's clipboard, so it runs with the other tests that do.</summary>
[Collection(ClipboardTests.Name)]
public class CardClipboardTests(SampleHost host) : CardTest(host)
{
    [Fact]
    public async Task CuttingEverythingInASurface_LeavesTheCards()
    {
        await OpenFixtureAsync("everything", "write");
        var cards = Page.Locator("[data-block-id='prose'] [data-rt-card]");
        var count = await cards.CountAsync();

        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > p"));
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("ControlOrMeta+x");
        await WaitUntilAcknowledgedAsync();

        await Expect(cards).ToHaveCountAsync(count);
        // Firefox removes the paragraph; the others leave it, empty, to type in. Everything else is as it was.
        var nodes = Nodes(await SaveAsync(Page), "prose");
        var kept = CompactAll(Nodes(Original("everything"), "prose")).ToList();
        kept.RemoveAt(13);
        CompactAll(nodes.Where(node => TextOf(node) != "" || (string)node!["kind"]! != "paragraph")).ShouldBe(kept);
    }
}

/// <summary>Helpers for tests of the cards.</summary>
public abstract class CardTest(SampleHost host) : SurfaceTest(host)
{
    protected ILocator Start => Surface("start");

    protected ILocator Toolbar => Page.GetByRole(AriaRole.Toolbar);

    protected ILocator Tool(string name) => Toolbar.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    protected ILocator Card(string blockId, string kind) => Page.Locator($"[data-block-id='{blockId}'] [data-rt-card={kind}]");

    /// <summary>The block's first code card's text area, once it can be typed into.</summary>
    protected async Task<ILocator> CodeAsync(string blockId)
    {
        var area = Page.Locator($"[data-block-id='{blockId}'] textarea[data-rt-code]").First;
        await Expect(area).ToHaveAttributeAsync("data-rt-ready", "true", new() { Timeout = 30_000 });
        return area;
    }

    protected async Task InsertAsync(string choice)
    {
        await Tool("Insert").ClickAsync();
        await Page.GetByRole(AriaRole.Group, new() { Name = "Insert", Exact = true })
            .GetByRole(AriaRole.Button, new() { Name = choice, Exact = true }).ClickAsync();
    }

    /// <summary>Compares as JSON, whatever order the properties are written in.</summary>
    protected static void ShouldBeNodes(string[] actual, params string[] expected) =>
        actual.Select(Canonical).ShouldBe(expected.Select(Canonical));

    private static string Canonical(string json) => Sorted(JsonNode.Parse(json))!.ToJsonString();

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
}
