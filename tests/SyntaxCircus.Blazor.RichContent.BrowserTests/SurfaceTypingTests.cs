using System.Text;
using System.Text.Json.Nodes;
using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using SyntaxCircus.RichContent;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>Showing a document in Write mode, typing in it and delivering what was typed.</summary>
public class SurfaceTypingTests(SampleHost host) : SurfaceTest(host)
{
    private const string Nbsp = "\u00a0";

    // The paragraph "Writing between two cards.": alone in the second surface of the first block.
    private const int BetweenCards = 13;

    private ILocator BetweenCardsParagraph => Surface("prose", 1).Locator(":scope > p");

    [Theory]
    [InlineData("everything")]
    [InlineData("unknown")]
    [InlineData("post")]
    public async Task Opening_AndClickingAround_RaisesNoChange(string fixture)
    {
        await OpenFixtureAsync(fixture, "write");
        await TapAsync();

        var surfaces = Page.Locator("[data-rt-surface]");
        var count = await surfaces.CountAsync();
        count.ShouldBeGreaterThan(0);
        for (var i = 0; i < count; i++)
        {
            await Expect(surfaces.Nth(i)).ToHaveAttributeAsync("contenteditable", "true");
            var elements = surfaces.Nth(i).Locator(":scope > *");
            var shown = Math.Min(await elements.CountAsync(), 14);
            for (var j = 0; j < shown; j++)
            {
                await elements.Nth(j).ClickAsync();
                await Page.Keyboard.PressAsync("End");
                await Page.Keyboard.PressAsync("ArrowLeft");
                await Page.Keyboard.PressAsync("Home");
                await Page.Keyboard.PressAsync("Shift+ArrowRight");
            }
        }

        await Page.Keyboard.PressAsync("Tab");

        (await SaveAsync(Page)).ShouldBe(Original(fixture));
        (await ChangeCountAsync(Page)).ShouldBe(0);
        (await SentPatchesAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "")]
    [InlineData(8, "ol > li:first-child")]
    [InlineData(9, "li:nth-child(2) > p:nth-child(2)")]
    [InlineData(10, "li")]
    [InlineData(11, "ul > li")]
    public async Task Typing_ChangesOnlyThatNode(int node, string inner)
    {
        await OpenFixtureAsync("everything", "write");
        var element = Surface("prose").Locator($":scope > :nth-child({node + 1})");
        var target = inner.Length == 0 ? element : element.Locator($":scope {inner}").First;

        await PlaceCaretAsync(target);
        await Page.Keyboard.TypeAsync("Zed");

        var saved = await SaveAsync(Page);
        var changed = AssertOnlyNodesReplaced(Original("everything"), saved, "prose", node, 1, 1).Single();
        TextOf(changed).ShouldContain("Zed");
        TextOf(changed).Replace("Zed", "").ShouldBe(TextOf(Nodes(Original("everything"), "prose")[node]));
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("everything", false)]
    [InlineData("everything", true)]
    [InlineData("post", false)]
    [InlineData("unknown", false)]
    public async Task RebuildingEveryNode_FromTheScreen_KeepsWhatIsStored(string fixture, bool strictCsp)
    {
        await OpenFixtureAsync(fixture, "write", strictCsp);

        // Every top-level node is changed and changed back, so each one is rebuilt from the DOM.
        await Page.EvaluateAsync("""
            () => {
                for (const surface of document.querySelectorAll('[data-rt-surface]')) {
                    for (const element of surface.children) {
                        const text = document.createTreeWalker(element, NodeFilter.SHOW_TEXT).nextNode();
                        text.appendData('x');
                        surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                        text.deleteData(text.length - 1, 1);
                        surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                    }
                }
            }
            """);
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        var expected = Original(fixture);
        if (fixture == "everything")
        {
            // The one allowed difference: a rebuilt node's adjacent text runs merge.
            const string runs = """{"kind":"text","text":"Two adjacent "},{"kind":"text","text":"text runs."}""";
            expected.ShouldContain(runs);
            expected = expected.Replace(runs, """{"kind":"text","text":"Two adjacent text runs."}""");
        }

        saved.ShouldBe(expected);
        (await ChangeCountAsync(Page)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SaveClickedImmediatelyAfterTyping_HasTheText()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph);
        var save = await SaveNumberAsync(Page);

        await Page.EvaluateAsync("""
            () => {
                document.execCommand('insertText', false, ' IMMEDIATE');
                document.getElementById('save').click();
            }
            """);
        await WaitForSaveAsync(Page, save);

        var saved = await Page.Locator("#saved").TextContentAsync();
        TextOf(Nodes(saved!, "prose")[BetweenCards]).ShouldBe("Writing between two cards. IMMEDIATE");
    }

    [Fact]
    public async Task SaveClickedWithTheMouseRightAfterKeystrokes_HasTheText()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph);

        await Page.Keyboard.TypeAsync(" typed quickly, every key a message");
        var saved = await SaveAsync(Page);

        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards. typed quickly, every key a message");
    }

    [Fact]
    public async Task EditsInTwoBlocks_BothReachTheDocument()
    {
        await OpenFixtureAsync("everything", "write");

        await Page.EvaluateAsync("""
            () => {
                // Many edits in one task, in two blocks, so that renders cannot keep up between them.
                const surfaces = [...document.querySelectorAll('[data-rt-surface]')];
                for (let i = 0; i < 30; i++) {
                    for (const surface of [surfaces[1], surfaces[3]]) {
                        surface.focus();
                        const text = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT).nextNode();
                        text.appendData('.');
                        surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                    }
                }
            }
            """);
        await WaitUntilReadyAsync();
        var saved = await SaveAsync(Page);

        var dots = new string('.', 30);
        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards." + dots);
        TextOf(Nodes(saved, "more-prose")[0]).ShouldBe("A second rich text block, next to the first." + dots);

        Task WaitUntilReadyAsync() => Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('[data-rt-surface]')].every(e => e.richContentSurface.transport.unacknowledged === 0)");
    }

    [Theory]
    [InlineData(8, new[] { "Writing ", "between two cards." })]
    [InlineData(0, new[] { "", "Writing between two cards." })]
    [InlineData(-1, new[] { "Writing between two cards.", "Next" })]
    public async Task Enter_SplitsParagraph_NoLostOrDuplicateNodes(int at, string[] expected)
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph, at);

        await Page.Keyboard.PressAsync("Enter");
        if (at < 0)
        {
            await Page.Keyboard.TypeAsync("Next");
        }

        var saved = await SaveAsync(Page);

        var nodes = AssertOnlyNodesReplaced(Original("everything"), saved, "prose", BetweenCards, 1, 2);
        nodes.Select(node => (string?)node!["kind"]).ShouldBe(["paragraph", "paragraph"]);
        nodes.Select(TextOf).ShouldBe(expected);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Enter_ThenTypingOnBothSides_KeepsEachInItsOwnNode()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph, 8);

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("2:");
        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync("(1)");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("middle");

        var nodes = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", BetweenCards, 1, 3);
        nodes.Select(TextOf).ShouldBe(["Writing (1)", "middle", "2:between two cards."]);
    }

    [Fact]
    public async Task Enter_AtTheEndOfAHeading_LeavesTheHeading_AndAddsAParagraph()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("After");

        var added = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 2, 0, 1).Single();
        Compact(added).ShouldBe("""{"kind":"paragraph","children":[{"kind":"text","text":"After"}]}""");
    }

    [Fact]
    public async Task Enter_InAList_AddsAnItem_AndKeepsTheMissingOrdering()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(11) > li"));

        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("Second item");

        var list = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 10, 1, 1).Single()!;
        list["ordered"].ShouldBeNull();
        list["children"]!.AsArray().Select(item => (string?)item!["kind"]).ShouldBe(["listItem", "listItem"]);
        list["children"]!.AsArray().Select(TextOf).ShouldBe(["A top-level list with no ordering stored", "Second item"]);
    }

    [Fact]
    public async Task Enter_InsideALink_GivesBothHalvesTheAddress()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > :nth-child(5) a"), 2);

        await Page.Keyboard.PressAsync("Enter");

        var nodes = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 4, 1, 2);
        nodes.ShouldAllBe(node => Compact(node).Contains("\"kind\":\"link\",\"url\":\"/relative/page\""));
        string.Concat(nodes.Select(TextOf)).Replace(Nbsp, " ").ShouldBe(TextOf(Nodes(Original("everything"), "prose")[4]));
    }

    [Theory]
    // Joining a heading onto the paragraph before it, and a paragraph onto a heading: browsers may leave styled spans.
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    public async Task Backspace_AtTheStart_JoinsTwoNodes_WithoutLosingText(int node, int previous)
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(Surface("prose").Locator($":scope > :nth-child({node + 1})"), 0);

        var original = Nodes(Original("everything"), "prose");
        await Page.Keyboard.PressAsync("Backspace");
        await SkipWhereFirefoxLosesTextAsync(Surface("prose"), previous, TextOf(original[previous]) + TextOf(original[node]));

        var joined = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", previous, 2, 1).Single();
        TextOf(joined).Replace(Nbsp, " ").ShouldBe(TextOf(original[previous]) + TextOf(original[node]));
        Compact(joined).ShouldNotContain("style");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingASelectionAcrossNodes_KeepsTheRest()
    {
        await OpenFixtureAsync("everything", "write");
        // From inside the first paragraph to inside the subheading.
        await Surface("prose").EvaluateAsync("""
            surface => {
                surface.focus();
                const start = surface.children[0].firstChild;
                const end = surface.children[2].firstChild;
                getSelection().setBaseAndExtent(start, 'A plain'.length, end, 'A subheading'.length);
            }
            """);

        await Page.Keyboard.PressAsync("Delete");
        await SkipWhereFirefoxLosesTextAsync(Surface("prose"), 0, "A plain with code");

        var remaining = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", 0, 3, 1).Single();
        TextOf(remaining).Replace(Nbsp, " ").ShouldBe("A plain with code");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task UndoingAnEnter_RestoresTheText_InTheDocument()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph, "Writing ".Length);
        await Page.Keyboard.PressAsync("Enter");
        Nodes(await SaveAsync(Page), "prose").Count.ShouldBe(Nodes(Original("everything"), "prose").Count + 1);

        await Surface("prose", 1).FocusAsync();
        await Page.EvaluateAsync("() => document.execCommand('undo')");

        var saved = await SaveAsync(Page);
        var node = AssertOnlyNodesReplaced(Original("everything"), saved, "prose", BetweenCards, 1, 1).Single();
        TextOf(node).ShouldBe("Writing between two cards.");
    }

    [Fact]
    public async Task BrowserMarkup_IsReducedToModelNodes()
    {
        await OpenFixtureAsync("everything", "write");

        await Surface("prose", 1).EvaluateAsync("""
            surface => {
                const make = (tag, text, attributes = {}) => {
                    const element = document.createElement(tag);
                    for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
                    element.textContent = text;
                    return element;
                };
                const paragraph = surface.firstElementChild;
                const font = make('font', '', { face: 'serif' });
                font.append(make('b', 'bold'), ' ', make('i', 'italic'));
                paragraph.append(make('span', ' styled ', { style: 'color: red; font-weight: bold' }), font, make('br', ''));
                const division = make('div', '');
                division.append('from ', make('span', 'a div'), make('br', ''));
                const style = make('style', 'p { --not: content }');
                surface.append(division, make('h1', 'too big'), 'loose text', style, make('br', ''));
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);

        var nodes = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", BetweenCards, 1, 4);
        CompactAll(nodes).ShouldBe(
        [
            """{"kind":"paragraph","children":[{"kind":"text","text":"Writing between two cards. styled "},{"kind":"strong","children":[{"kind":"text","text":"bold"}]},{"kind":"text","text":" "},{"kind":"emphasis","children":[{"kind":"text","text":"italic"}]}]}""",
            """{"kind":"paragraph","children":[{"kind":"text","text":"from a div"}]}""",
            """{"kind":"paragraph","children":[{"kind":"text","text":"too big"}]}""",
            """{"kind":"paragraph","children":[{"kind":"text","text":"loose text"}]}""",
        ]);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SpacesAtBoundaries_AreOrdinarySpaces()
    {
        await OpenFixtureAsync("everything", "write");

        await PlaceCaretAsync(BetweenCardsParagraph, 0);
        await Page.Keyboard.TypeAsync("  ");
        await PlaceCaretAsync(BetweenCardsParagraph);
        await Page.Keyboard.TypeAsync("   ");
        await PlaceCaretAsync(BetweenCardsParagraph, "  Writing".Length);
        await Page.Keyboard.TypeAsync("  ");
        // Deleting a word leaves the two spaces around it side by side.
        await PlaceCaretAsync(BetweenCardsParagraph, "  Writing   between two".Length);
        for (var i = 0; i < "two".Length; i++)
        {
            await Page.Keyboard.PressAsync("Backspace");
        }

        // Splitting next to a space is where some browsers swap it for a non-breaking one.
        await PlaceCaretAsync(BetweenCardsParagraph, "  Writing   ".Length);
        await Page.Keyboard.PressAsync("Enter");
        await PlaceCaretAsync(Surface("prose", 1).Locator(":scope > p").Nth(1));
        await Page.Keyboard.TypeAsync("end ");

        var nodes = AssertOnlyNodesReplaced(Original("everything"), await SaveAsync(Page), "prose", BetweenCards, 1, 2);
        nodes.Select(TextOf).ShouldBe(["  Writing   ", "between  cards.   end "]);
    }

    [Fact]
    public async Task NonBreakingSpacesTheBrowserInserts_BecomeSpaces()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph);

        // What a browser that does not preserve white space inserts for spaces at a boundary.
        await Page.EvaluateAsync("() => document.execCommand('insertText', false, '\\u00a0\\u00a0more\\u00a0')");

        var saved = await SaveAsync(Page);
        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards.  more ");
    }

    [Fact]
    public async Task StoredNonBreakingSpace_Survives()
    {
        const int node = 5;
        var stored = TextOf(Nodes(Original("everything"), "prose")[node]);
        stored.ShouldBe($"Keep 10{Nbsp}km and{Nbsp}{Nbsp}two together.");
        await OpenFixtureAsync("everything", "write");
        var paragraph = Surface("prose").Locator($":scope > :nth-child({node + 1})");

        await PlaceCaretAsync(paragraph);
        await Page.Keyboard.TypeAsync(" x");
        await PlaceCaretAsync(paragraph, 0);
        await Page.Keyboard.TypeAsync("y ");
        TextOf(Nodes(await SaveAsync(Page), "prose")[node]).ShouldBe($"y {stored} x");

        // Splitting next to them, and between them.
        await PlaceCaretAsync(paragraph, "y Keep 10".Length);
        await Page.Keyboard.PressAsync("Enter");
        var halves = Nodes(await SaveAsync(Page), "prose").Skip(node).Take(2).Select(TextOf).ToArray();
        halves.ShouldBe(["y Keep 10", $"{Nbsp}km and{Nbsp}{Nbsp}two together. x"]);
    }

    [Fact]
    public async Task StoredNonBreakingSpace_SurvivesDeletingAndUndoing()
    {
        const int node = 5;
        await OpenFixtureAsync("everything", "write");
        var paragraph = Surface("prose").Locator($":scope > :nth-child({node + 1})");
        await PlaceCaretAsync(paragraph, "Keep ".Length);
        for (var i = 0; i < "10 km".Length; i++)
        {
            await Page.Keyboard.PressAsync("Shift+ArrowRight");
        }

        await Page.Keyboard.PressAsync("Delete");
        TextOf(Nodes(await SaveAsync(Page), "prose")[node]).ShouldBe($"Keep  and{Nbsp}{Nbsp}two together.");
        await Surface("prose").FocusAsync();
        await Page.EvaluateAsync("() => document.execCommand('undo')");

        TextOf(Nodes(await SaveAsync(Page), "prose")[node]).ShouldBe($"Keep 10{Nbsp}km and{Nbsp}{Nbsp}two together.");
    }

    [Fact]
    public async Task Composition_ThenSave_HasTextOnce()
    {
        if (Host.Browser.BrowserType.Name != "chromium")
        {
            Assert.Skip("Only Chromium's protocol can drive an input method; see Composition_SendsNothingUntilItEnds.");
        }

        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph);
        await TapAsync();
        var session = await Page.Context.NewCDPSessionAsync(Page);

        foreach (var composing in new[] { "k", "か", "かん", "かんじ" })
        {
            await session.SendAsync("Input.imeSetComposition", new()
            {
                ["text"] = composing,
                ["selectionStart"] = composing.Length,
                ["selectionEnd"] = composing.Length,
            });
        }

        (await SentPatchesAsync()).ShouldBe(0);
        await session.SendAsync("Input.insertText", new() { ["text"] = "漢字" });
        var saved = await SaveAsync(Page);

        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards.漢字");
        (await ChangeCountAsync(Page)).ShouldBe(1);
    }

    [Fact]
    public async Task Composition_EndedByClickingSave_HasTextOnce()
    {
        if (Host.Browser.BrowserType.Name != "chromium")
        {
            Assert.Skip("Only Chromium's protocol can drive an input method; see Composition_SendsNothingUntilItEnds.");
        }

        // A short page: where the caret is off screen, Chromium scrolls back to it while ending the composition,
        // between the mouse button going down and up, and the click misses.
        await OpenFixtureAsync("empty", "write");
        await Surface("start").ClickAsync();
        var session = await Page.Context.NewCDPSessionAsync(Page);
        await session.SendAsync("Input.imeSetComposition", new() { ["text"] = "かん", ["selectionStart"] = 2, ["selectionEnd"] = 2 });

        // The click takes the focus, which ends the composition before the click is handled.
        var saved = await SaveAsync(Page);

        TextOf(Nodes(saved, "start").ShouldHaveSingleItem()).ShouldBe("かん");
        (await SaveAsync(Page)).ShouldBe(saved);
    }

    [Fact]
    public async Task Composition_SendsNothingUntilItEnds()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync();
        var save = await SaveNumberAsync(Page);

        // The events an input method raises, on every engine. A scripted click does not move the focus.
        await BetweenCardsParagraph.EvaluateAsync("""
            paragraph => {
                const surface = paragraph.parentElement;
                surface.focus();
                surface.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
                paragraph.firstChild.appendData('かん');
                surface.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true, inputType: 'insertCompositionText' }));
                document.getElementById('save').click();
            }
            """);
        await WaitForSaveAsync(Page, save);
        (await SentPatchesAsync()).ShouldBe(0);
        (await ChangeCountAsync(Page)).ShouldBe(0);

        save = await SaveNumberAsync(Page);
        await BetweenCardsParagraph.EvaluateAsync("""
            paragraph => {
                paragraph.firstChild.data = paragraph.firstChild.data.replace('かん', '漢');
                paragraph.parentElement.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }));
                document.getElementById('save').click();
            }
            """);
        await WaitForSaveAsync(Page, save);

        var saved = await Page.Locator("#saved").TextContentAsync();
        TextOf(Nodes(saved!, "prose")[BetweenCards]).ShouldBe("Writing between two cards.漢");
        (await SentPatchesAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task StoredMarkupCharacters_AreShownAsText()
    {
        await OpenFixtureAsync("everything", "write");

        var paragraph = Surface("prose").Locator(":scope > :nth-child(8)");

        await Expect(paragraph).ToHaveTextAsync("Characters that look like markup: <b>not bold</b> & \"quotes\" \\ backslash.");
        await Expect(paragraph.Locator("b")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TypingIntoAnEmptyDocument_StoresAParagraph()
    {
        await OpenFixtureAsync("empty", "write");

        await Surface("start").ClickAsync();
        (await SaveAsync(Page)).ShouldBe(Original("empty"));
        await Surface("start").ClickAsync();
        await Page.Keyboard.TypeAsync("First words ");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("more");

        var saved = await SaveAsync(Page);
        CompactAll(Nodes(saved, "start")).ShouldBe(
        [
            """{"kind":"paragraph","children":[{"kind":"text","text":"First words "}]}""",
            """{"kind":"paragraph","children":[{"kind":"text","text":"more"}]}""",
        ]);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingEverything_LeavesATypableSurface()
    {
        await OpenFixtureAsync("empty", "write");
        await Surface("start").ClickAsync();
        await Page.Keyboard.TypeAsync("gone");
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.PressAsync("Backspace");

        await Page.Keyboard.TypeAsync("kept");

        TextOf(Nodes(await SaveAsync(Page), "start").ShouldHaveSingleItem()).ShouldBe("kept");
    }

    [Fact]
    public async Task LargeEdit_OnDefaultHubOptions_KeepsCircuit()
    {
        await OpenFixtureAsync("empty", "write");
        await TapAsync();

        await InsertParagraphsAsync(1, new string('a', 40 * 1024));
        await WaitForChangesAsync(1);
        Nodes(await SaveAsync(Page), "start").Count.ShouldBe(1);

        await InsertParagraphsAsync(200, new string('b', 1024));
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        var nodes = Nodes(saved, "start");
        nodes.Count.ShouldBe(201);
        nodes.Sum(node => TextOf(node).Length).ShouldBe(240 * 1024);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
        await ShouldHaveSentSmallChunksAsync();
    }

    [Fact]
    public async Task LargeInsert_ThenImmediateSave_HasAllText()
    {
        await OpenFixtureAsync("empty", "write");
        var save = await SaveNumberAsync(Page);

        await InsertParagraphsAsync(200, new string('c', 1000), numbered: true, thenSave: true);
        await WaitForSaveAsync(Page, save);

        var nodes = Nodes((await Page.Locator("#saved").TextContentAsync())!, "start");
        nodes.Count.ShouldBe(200);
        nodes.ShouldAllBe(node => TextOf(node).Length == 1001);
    }

    [Fact]
    public async Task NonAsciiAndQuoteHeavyLargeEdit_KeepsCircuit()
    {
        // Characters that take the most room in a message: escaped twice, several bytes each, or surrogate pairs.
        // The leading emoji moves every later one, so chunk boundaries fall in the middle of pairs.
        const string unit = "😀中文\"quoted\" \\back\\ 'single' <&> \u0001\u2028";
        var paragraph = "x" + string.Concat(Enumerable.Repeat(unit, 25));
        var count = (100 * 1024 / Encoding.UTF8.GetByteCount(paragraph)) + 1;
        await OpenFixtureAsync("empty", "write");
        await TapAsync();

        await InsertParagraphsAsync(count, paragraph, numbered: true);
        await InsertParagraphsAsync(1, string.Concat(Enumerable.Repeat("😀", 6000)), numbered: true);
        await InsertParagraphsAsync(1, new string('"', 9000) + new string('\\', 9000) + new string('中', 9000), numbered: true);
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        var nodes = Nodes(saved, "start");
        nodes.Count.ShouldBe(count + 2);
        TextOf(nodes[0]).ShouldBe(paragraph + "0");
        TextOf(nodes[count]).ShouldBe(string.Concat(Enumerable.Repeat("😀", 6000)) + "0");
        TextOf(nodes[count + 1]).ShouldBe(new string('"', 9000) + new string('\\', 9000) + new string('中', 9000) + "0");
        await ShouldHaveSentSmallChunksAsync();
    }

    [Fact]
    public async Task StrictCsp_TypingWorks()
    {
        await OpenFixtureAsync("everything", "write", strictCsp: true);
        await PlaceCaretAsync(BetweenCardsParagraph);

        await Page.Keyboard.TypeAsync(" Strict");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("policy");
        await Page.Locator("#replace-other").ClickAsync();
        await Expect(Surface("prose").Locator(":scope > p").First).ToHaveTextAsync(Fixtures.ReplacementText);

        var nodes = Nodes(await SaveAsync(Page), "prose");
        nodes.Skip(BetweenCards).Take(2).Select(TextOf).ShouldBe(["Writing between two cards. Strict", "policy"]);
    }

    [Fact]
    public async Task Disabled_SurfacesCannotBeEdited()
    {
        await OpenFixtureAsync("everything", "write&disabled=true");
        await WaitForSurfacesAsync(Page);
        var surfaces = Page.Locator("[data-rt-surface]");

        for (var i = 0; i < await surfaces.CountAsync(); i++)
        {
            await Expect(surfaces.Nth(i)).ToHaveAttributeAsync("contenteditable", "false");
            await Expect(surfaces.Nth(i)).ToHaveAttributeAsync("aria-disabled", "true");
        }

        await BetweenCardsParagraph.ClickAsync();
        await Page.Keyboard.TypeAsync("nothing");
        // A link must not navigate even though the surface is not editable.
        // Forced: Playwright takes what is inside an aria-disabled element to be disabled.
        await Surface("prose").Locator("a").First.ClickAsync(new() { Force = true });

        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        new Uri(Page.Url).AbsolutePath.ShouldBe("/fixture/everything");
    }

    [Fact]
    public async Task ReplacingWithAnEqualCopy_LeavesTheSurfacesAlone()
    {
        await OpenFixtureAsync("everything", "write");
        await PlaceCaretAsync(BetweenCardsParagraph);
        await Page.Keyboard.TypeAsync("1");
        await WaitUntilAcknowledgedAsync();
        await Page.EvaluateAsync("() => { for (const e of document.querySelectorAll('[data-rt-surface] > *')) e.kept = true; }");

        await Page.Locator("#replace-same").ClickAsync();
        await SaveAsync(Page);

        (await Page.EvaluateAsync<bool>(
            "() => [...document.querySelectorAll('[data-rt-surface] > *')].every(e => e.kept)")).ShouldBeTrue();
        await PlaceCaretAsync(BetweenCardsParagraph);
        await Page.Keyboard.TypeAsync("2");
        TextOf(Nodes(await SaveAsync(Page), "prose")[BetweenCards]).ShouldBe("Writing between two cards.12");
    }

    [Fact]
    public async Task ReplacingWithDifferentContent_RedrawsOnlyThatSurface_AndTypingStillArrives()
    {
        await OpenFixtureAsync("everything", "write");
        await Page.EvaluateAsync("() => { for (const e of document.querySelectorAll('[data-rt-surface] > *')) e.kept = true; }");

        await Page.Locator("#replace-other").ClickAsync();
        var first = Surface("prose").Locator(":scope > p").First;
        await Expect(first).ToHaveTextAsync(Fixtures.ReplacementText);

        (await BetweenCardsParagraph.EvaluateAsync<bool>("e => e.kept === true")).ShouldBeTrue();
        await PlaceCaretAsync(first);
        await Page.Keyboard.TypeAsync(" And more.");
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldBe(Fixtures.ReplacementText + " And more.");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEditInFlightWhenTheContentIsReplaced_IsDroppedQuietly()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync("hold");
        await PlaceCaretAsync(Surface("prose").Locator(":scope > p").First);
        await Page.Keyboard.TypeAsync("x");

        await Page.Locator("#replace-other").ClickAsync();
        await Expect(Surface("prose").Locator(":scope > p").First).ToHaveTextAsync(Fixtures.ReplacementText);
        await Page.EvaluateAsync("() => window.release()");

        (await SaveAsync(Page)).ShouldBe(Serialized(Fixtures.Replaced(Fixtures.Get("everything")!)));
        (await ChangeCountAsync(Page)).ShouldBe(0);
        (await Page.EvaluateAsync<bool>("() => window.answers.every(a => a.answer === true)")).ShouldBeTrue();
    }

    // Firefox's own editing (seen in 159, in a bare contenteditable with no script) loses text when it joins two
    // blocks of different kinds, such as a heading and a paragraph: what follows the second block's first inline
    // element is deleted. Joins of two blocks of one kind are not affected. What the surface can promise there is
    // checked first: the document is what is on screen, and undoing brings the text back to both. Does nothing when
    // the element at the index holds the expected text, as it does everywhere else.
    private async Task SkipWhereFirefoxLosesTextAsync(ILocator surface, int index, string expected)
    {
        var kept = (await surface.Locator(":scope > *").Nth(index).TextContentAsync())!.Replace(Nbsp, " ") == expected;
        if (kept || Host.Browser.BrowserType.Name != "firefox")
        {
            return;
        }

        // The deleted text is missing from the screen and the document alike.
        var onScreen = await surface.EvaluateAsync<string[]>("surface => [...surface.children].map(e => e.textContent)");
        Nodes(await SaveAsync(Page), "prose").Take(onScreen.Length).Select(TextOf).ShouldBe(onScreen);
        await surface.FocusAsync();
        await Page.EvaluateAsync("() => document.execCommand('undo')");
        var undone = await SaveAsync(Page);
        CompactAll(Nodes(undone, "prose").Select(node => (JsonNode?)JsonValue.Create(TextOf(node))))
            .ShouldBe(CompactAll(Nodes(Original("everything"), "prose").Select(node => (JsonNode?)JsonValue.Create(TextOf(node)))));
        (await surface.EvaluateAsync<int>("surface => surface.textContent.length"))
            .ShouldBeGreaterThan(onScreen.Sum(text => text.Length));

        Assert.Skip("Firefox deletes text when its own editing joins blocks of different kinds; undo restores it.");
    }

    // Appends paragraphs to the first surface, as a paste would, and raises one input event for all of them. When
    // numbered, each paragraph's text ends with the last digit of its position.
    private async Task InsertParagraphsAsync(int count, string text, bool numbered = false, bool thenSave = false) =>
        await Page.Locator("[data-rt-surface]").First.EvaluateAsync(
            """
            (surface, [count, text, thenSave, numbered]) => {
                surface.focus();
                surface.querySelector('[data-rt-placeholder]')?.remove();
                const fragment = document.createDocumentFragment();
                for (let i = 0; i < count; i++) {
                    const paragraph = document.createElement('p');
                    paragraph.textContent = numbered ? text + (i % 10) : text;
                    fragment.append(paragraph);
                }

                surface.append(fragment);
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                if (thenSave) document.getElementById('save').click();
            }
            """,
            new object[] { count, text, thenSave, numbered });

    private async Task ShouldHaveSentSmallChunksAsync()
    {
        var lengths = await Page.EvaluateAsync<int[]>("() => window.sent.map(m => m.chunk.length)");
        lengths.Length.ShouldBeGreaterThan(10);
        lengths.ShouldAllBe(length => length <= 4000);
        (await Page.EvaluateAsync<bool>(
            "() => window.sent.every(m => !/[\\uD800-\\uDBFF]$/.test(m.chunk) && !/^[\\uDC00-\\uDFFF]/.test(m.chunk))")).ShouldBeTrue();
    }
}
