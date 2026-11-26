using System.Text.Json.Nodes;
using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>What happens when edits do not get through, or cannot be converted.</summary>
public class SurfaceDeliveryTests(SampleHost host) : SurfaceTest(host)
{
    // Of the first block's first surface: the "A heading" element, and its node's position.
    private const int Heading = 1;
    private const int BetweenCards = 13;

    private ILocator Paragraph => Surface("prose", 1).Locator(":scope > p");

    // Makes an edit and takes it back, in one task, so the second is sent before the first's failure is known.
    private const string TypeThenRevert = """
        paragraph => {
            const input = () => paragraph.parentElement.dispatchEvent(new InputEvent('input', { bubbles: true }));
            paragraph.firstChild.appendData('X');
            input();
            paragraph.firstChild.deleteData(paragraph.firstChild.length - 1, 1);
            input();
        }
        """;

    private const string RemoveThenRestore = """
        heading => {
            const surface = heading.parentElement;
            const input = () => surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            const next = heading.nextSibling;
            heading.remove();
            input();
            surface.insertBefore(heading, next);
            input();
        }
        """;

    [Theory]
    [InlineData("lose-reply", "drop")]
    [InlineData("lose-reply", "lose-reply")]
    [InlineData("lose-reply", "pass")]
    [InlineData("drop", "drop")]
    public async Task FailedSend_ThenEditReverted_Converges(string first, string second)
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync(first, second);

        await Paragraph.EvaluateAsync(TypeThenRevert);

        await ShouldConvergeOnTheOriginalAsync();
    }

    [Theory]
    [InlineData("lose-reply", "drop")]
    [InlineData("lose-reply", "lose-reply")]
    [InlineData("lose-reply", "pass")]
    [InlineData("drop", "drop")]
    public async Task FailedSend_ThenRemovedNodeRestored_Converges(string first, string second)
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync(first, second);

        await Surface("prose").Locator(":scope > h2").EvaluateAsync(RemoveThenRestore);

        await ShouldConvergeOnTheOriginalAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditTakenBack_AfterTheFailureIsKnown_Converges(bool removal)
    {
        await OpenFixtureAsync("everything", "write");
        // The first patch arrives but seems to fail; every retry before the second edit is lost.
        await TapAsync("lose-reply", "drop", "drop", "drop", "drop", "drop", "drop");
        var target = removal ? Surface("prose").Locator(":scope > h2") : Paragraph;
        await target.EvaluateAsync(removal
            ? "e => { window.removed = [e, e.parentElement, e.nextSibling]; e.remove(); window.removed[1].dispatchEvent(new InputEvent('input', { bubbles: true })); }"
            : "e => { e.firstChild.appendData('X'); e.parentElement.dispatchEvent(new InputEvent('input', { bubbles: true })); }");
        await WaitForChangesAsync(1);
        await Page.WaitForFunctionAsync("() => new Set(window.sent.map(m => m.sequence)).size >= 2");

        await Page.EvaluateAsync("() => { window.plan = []; }");
        await target.Or(Page.Locator("#save")).First.EvaluateAsync(removal
            ? "() => { const [e, parent, next] = window.removed; parent.insertBefore(e, next); parent.dispatchEvent(new InputEvent('input', { bubbles: true })); }"
            : "() => { const p = document.querySelectorAll('[data-rt-surface]')[1].querySelector('p'); p.firstChild.deleteData(p.firstChild.length - 1, 1); p.parentElement.dispatchEvent(new InputEvent('input', { bubbles: true })); }");

        await ShouldConvergeOnTheOriginalAsync();
    }

    [Fact]
    public async Task APatchAfterAFailure_AlsoSendsEverythingUnacknowledged()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync("drop");
        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync("a");
        await WaitUntilAcknowledgedAsync();

        var patches = await PatchesAsync();

        patches.Length.ShouldBe(2);
        Changed(patches[1]).ShouldBe(Changed(patches[0]));
    }

    [Fact]
    public async Task FailedSend_IsRetriedUntilItSucceeds_WithoutMoreTyping()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync("drop", "drop", "drop", "drop");
        await PlaceCaretAsync(Paragraph);

        await Page.Keyboard.TypeAsync("!");
        await WaitUntilAcknowledgedAsync();

        (await SentPatchesAsync()).ShouldBe(5);
        TextOf(Nodes(await SaveAsync(Page), "prose")[BetweenCards]).ShouldBe("Writing between two cards.!");
    }

    [Fact]
    public async Task ACallThatNeverSettles_IsSentAgain()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync("hang");
        await PlaceCaretAsync(Paragraph);

        await Page.Keyboard.TypeAsync("!");

        await WaitUntilAcknowledgedAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[BetweenCards]).ShouldBe("Writing between two cards.!");
    }

    [Fact]
    public async Task AClickAfterACallHasHung_SendsItAgainFirst()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync("hang");
        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync("!");
        // Longer than a click waits before resending, shorter than the surface would wait on its own.
        await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
        (await SentPatchesAsync()).ShouldBe(1);

        var saved = await SaveAsync(Page);

        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards.!");
    }

    // Nesting lists too deeply gives content the surface cannot convert.
    private const string NestTooDeeply = """
        paragraph => {
            const surface = paragraph.parentElement;
            let list = document.createElement('ul');
            list.id = 'deep';
            surface.append(list);
            for (let level = 0; level < 4; level++) {
                const item = list.appendChild(document.createElement('li'));
                item.append('level ' + level);
                if (level < 3) list = item.appendChild(document.createElement('ul'));
            }

            surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
        }
        """;

    [Fact]
    public async Task RejectedPatch_IsNotRetried_AndTheNextChangeSendsEverything()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync();

        await Paragraph.EvaluateAsync(NestTooDeeply);
        await Page.WaitForFunctionAsync("() => window.answers.some(a => a.answer === false)");
        // Longer than a retry or a stall would take.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        (await SentPatchesAsync()).ShouldBe(1);
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await SentPatchesAsync()).ShouldBe(1);

        // An edit elsewhere cannot be applied either while the content cannot be converted: it is one patch.
        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync("!");
        await Page.WaitForFunctionAsync("() => window.answers.filter(a => a.answer === false).length === 2");
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        (await Page.Locator("#deep").CountAsync()).ShouldBe(1, "what was typed stays on screen");

        // Once it can be converted, the next patch carries what was rejected before, "!" included.
        await Page.EvaluateAsync("""
            () => {
                const deep = document.getElementById('deep');
                const surface = deep.parentElement;
                deep.querySelector('ul ul ul ul').remove();
                surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);
        await WaitUntilAcknowledgedAsync();
        var nodes = Nodes(await SaveAsync(Page), "prose");

        TextOf(nodes[BetweenCards]).ShouldBe("Writing between two cards.!");
        TextOf(nodes[BetweenCards + 1]).ShouldBe("level 0level 1level 2");
        (await SentPatchesAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task APatchBuiltOnARejectedOne_IsFollowedByOneThatSaysEverything_Once()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync();

        // Sent together: the second builds on a node that only the rejected first one mentions.
        await Paragraph.EvaluateAsync("""
            paragraph => {
                const surface = paragraph.parentElement;
                const input = () => surface.dispatchEvent(new InputEvent('input', { bubbles: true }));
                const quote = surface.appendChild(document.createElement('blockquote'));
                let list = quote.appendChild(document.createElement('ul'));
                for (let level = 0; level < 4; level++) {
                    const item = list.appendChild(document.createElement('li'));
                    item.append('level ' + level);
                    if (level < 3) list = item.appendChild(document.createElement('ul'));
                }

                input();
                paragraph.firstChild.appendData('!');
                input();
            }
            """);
        await Page.WaitForFunctionAsync("() => window.answers.filter(a => a.answer === false).length >= 3");
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        (await SentPatchesAsync()).ShouldBe(3);
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"generation\":99,\"order\":[],\"changed\":[]}")]
    [InlineData("{\"generation\":1,\"order\":[\"nope\"],\"changed\":[]}")]
    public async Task AMalformedPayload_IsRefused_AndTheCircuitLives(string payload)
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync();

        var answer = await Surface("prose", 1).EvaluateAsync<bool>(
            "(surface, payload) => surface.richContentSurface.transport.invoke(1000000, 0, 1, payload)", payload);
        var gap = await Surface("prose", 1).EvaluateAsync<bool>(
            "(surface, payload) => surface.richContentSurface.transport.invoke(1000001, 1, 2, payload)", payload);

        answer.ShouldBeFalse();
        gap.ShouldBeFalse();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
    }

    [Fact]
    public async Task ARedrawTheBrowserNeverApplied_IsSentAgain_WhenTheOldContentIsEdited()
    {
        await OpenFixtureAsync("everything", "write");
        await TapAsync();
        var surface = Surface("prose");
        var first = surface.Locator(":scope > p").First;
        // Loses the redraw: the script holds it back, as during composition, and it is then thrown away.
        await surface.EvaluateAsync("element => { element.richContentSurface.composing = true; }");
        await Page.Locator("#replace-other").ClickAsync();
        await Page.WaitForFunctionAsync("() => !!document.querySelector('[data-rt-surface]').richContentSurface.pending");
        await surface.EvaluateAsync(
            "element => { element.richContentSurface.pending = null; element.richContentSurface.composing = false; }");
        (await first.TextContentAsync())!.ShouldStartWith("A plain paragraph");

        await PlaceCaretAsync(first);
        await Page.Keyboard.TypeAsync("x");

        // The replacement wins in the surface that differed, and it is drawn this time.
        await Expect(first).ToHaveTextAsync(Fixtures.ReplacementText);
        // The redraw is sent before the edit is answered, so the new text can show first.
        await Page.WaitForFunctionAsync("() => window.answers.length > 0");
        (await Page.EvaluateAsync<bool[]>("() => window.answers.map(a => a.answer)")).ShouldBe([false]);
        (await SaveAsync(Page)).ShouldBe(Serialized(Fixtures.Replaced(Parse(Original("everything")))));
        await PlaceCaretAsync(first);
        await Page.Keyboard.TypeAsync(" Kept.");
        await WaitUntilAcknowledgedAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[0]).ShouldBe(Fixtures.ReplacementText + " Kept.");
    }

    // A reconnect element of the host's own, as the project templates add. Blazor only sets classes on it.
    private const string AddOwnReconnectElement = """
        () => {
            const style = document.head.appendChild(document.createElement('style'));
            style.textContent = '#components-reconnect-modal { display: none } '
                + '#components-reconnect-modal.components-reconnect-show, #components-reconnect-modal.components-reconnect-retrying, '
                + '#components-reconnect-modal.components-reconnect-failed { display: block }';
            document.body.appendChild(document.createElement('div')).id = 'components-reconnect-modal';
        }
        """;

    [Fact]
    public async Task AReconnectElementThatCanAlwaysBeSeen_DoesNotHoldEditsBack()
    {
        await OpenFixtureAsync("everything", "write");
        await Page.EvaluateAsync("() => { const e = document.body.appendChild(document.createElement('div')); e.id = 'components-reconnect-modal'; e.textContent = 'visible'; }");

        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync("!");

        TextOf(Nodes(await SaveAsync(Page), "prose")[BetweenCards]).ShouldBe("Writing between two cards.!");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TextTypedWhileDisconnected_ArrivesAfterReconnecting(bool strictCsp, bool ownReconnectElement)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        if (ownReconnectElement)
        {
            await Page.EvaluateAsync(AddOwnReconnectElement);
        }

        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync("1");
        await WaitUntilAcknowledgedAsync();

        // The watch fails the test on any uncaught error or unhandled rejection, during the outage too.
        var outage = await Watch.DropConnectionAsync(Page);
        // Without one of its own, the page gets Blazor's default element, which is known by its shadow root.
        (await Page.EvaluateAsync<bool>("() => document.getElementById('components-reconnect-modal').shadowRoot !== null"))
            .ShouldBe(!ownReconnectElement);
        await Paragraph.EvaluateAsync("""
            paragraph => {
                for (const character of ' offline') {
                    paragraph.firstChild.appendData(character);
                    paragraph.parentElement.dispatchEvent(new InputEvent('input', { bubbles: true }));
                }
            }
            """);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();

        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);
        TextOf(Nodes(saved, "prose")[BetweenCards]).ShouldBe("Writing between two cards.1 offline");
    }

    private static string[] Changed(JsonObject patch) =>
        [.. patch["changed"]!.AsArray().Select(entry => (string)entry!["key"]!)];

    private async Task ShouldConvergeOnTheOriginalAsync()
    {
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);

        saved.ShouldBe(Original("everything"));
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();

        // Nothing is left out of step: a later edit still applies.
        await PlaceCaretAsync(Paragraph);
        await Page.Keyboard.TypeAsync(".");
        await WaitUntilAcknowledgedAsync();
        TextOf(Nodes(await SaveAsync(Page), "prose")[BetweenCards]).ShouldBe("Writing between two cards..");
        TextOf(Nodes(await SaveAsync(Page), "prose")[Heading]).ShouldBe("A heading");
    }
}
