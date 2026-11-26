using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// An editor whose mode is changed while it is on the page. The editor's element stays, while the toolbar and the
/// writing surfaces go and come back, and nothing the script kept for the ones that went may touch what is there now.
/// </summary>
public class ModeSwitchTests(SampleHost host) : CardTest(host)
{
    private ILocator Editor => Page.Locator(".doc-editor");

    // The text fields of the editor that cannot be typed into, by their labels.
    private Task<string[]> ReadOnlyFieldsAsync() =>
        Editor.EvaluateAsync<string[]>("""
            editor => [...editor.querySelectorAll('textarea, input[type="text"], input:not([type])')]
                .filter(field => field.readOnly)
                .map(field => field.closest('label')?.textContent.trim() || field.getAttribute('aria-label') || field.outerHTML)
            """);

    // Long enough for anything that was going to release the editor to have done so.
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

    private async Task SwitchToBlocksAsync()
    {
        await Page.Locator("#mode-blocks").ClickAsync();
        await Expect(Page.Locator("[data-rt-surface]")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Toolbar)).ToHaveCountAsync(0);
    }

    private async Task SwitchToWriteAsync()
    {
        await Page.Locator("#mode-write").ClickAsync();
        await ShouldBeReadyAsync();
    }

    private async Task ShouldBeReadyAsync()
    {
        await WaitForSurfacesAsync(Page);
        await Expect(Page.Locator("textarea[data-rt-code]:not([data-rt-ready])")).ToHaveCountAsync(0);
        // The toolbar's own script starts apart from the surfaces', and is what a click on a card relies on.
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.doc-editor').richContentFreeze?.area === document.querySelector('[data-rt-toolbar-area]')",
            null,
            new() { Timeout = 5000 });
    }

    private async Task ShouldBeEditableAsync()
    {
        await Expect(Page.Locator(ReadOnlySurfaces)).ToHaveCountAsync(0);
        await Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.doc-editor textarea, .doc-editor input')].every(field => !field.readOnly)",
            null,
            new() { Timeout = 5000 });
    }

    private ILocator Header(string blockId) => Page.Locator($"[data-block-id='{blockId}'] > header");

    private static ILocator Button(ILocator within, string name) =>
        within.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).First;

    private ILocator CtaBody => Page.Locator("[data-block-id=cta]").GetByLabel("Body");

    private static string Block(string json, string id) =>
        Compact(JsonNode.Parse(json)!["blocks"]!.AsArray().Single(block => (string?)block!["id"] == id));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AfterSwitchingToBlocksAndBack_RearrangingTheToolbarAndInsertAllWork(int times)
    {
        await OpenFixtureAsync("everything", "write");
        await CodeAsync("prose");
        for (var i = 0; i < times; i++)
        {
            await SwitchToBlocksAsync();
            await SwitchToWriteAsync();
        }

        (await ChangeCountAsync(Page)).ShouldBe(0, "switching raises no change");

        // A card's button, which freezes the editor until the new toolbar answers.
        await Button(Card("prose", "code").First, "Move up").ClickAsync();
        await WaitForChangesAsync(1);
        await ShouldBeEditableAsync();

        // A block's button.
        await Button(Header("callout-warning"), "Move up").ClickAsync();
        await WaitForChangesAsync(2);
        await ShouldBeEditableAsync();

        // A toolbar command, the link box and the keyboard way to the toolbar and back.
        await ShouldBeReadyAsync();
        var heading = Surface("prose").Locator(":scope > h2");
        await PlaceCaretAsync(heading);
        await Page.Keyboard.PressAsync("Shift+Home");
        await Tool("Italic").ClickAsync();
        await Expect(Tool("Italic")).ToHaveAttributeAsync("aria-pressed", "true");
        await Tool("Link").ClickAsync();
        await Page.GetByLabel("Link address").FillAsync("/after-switch");
        await Page.GetByLabel("Link address").PressAsync("Enter");
        await Expect(Page.GetByLabel("Link address")).ToHaveCountAsync(0);
        await Expect(heading.Locator("a")).ToHaveCountAsync(1);
        await PlaceCaretAsync(heading, 0);
        await Page.Keyboard.PressAsync("Alt+F10");
        await Expect(Tool("Bold")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Surface("prose")).ToBeFocusedAsync();

        // Insert.
        await PlaceCaretAsync(heading);
        await InsertAsync("Code block");
        var inserted = Page.Locator("[data-block-id='prose'] textarea[data-rt-code]").First;
        await Expect(inserted).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("after");
        await ShouldBeEditableAsync();

        var saved = await SaveAsync(Page);
        Compact(Nodes(saved, "prose")[1]).ShouldContain("/after-switch");
        Compact(Nodes(saved, "prose")[2]).ShouldContain("after");
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InBlocksMode_AfterWriteMode_MovingAndRemovingLeaveEveryFieldEditable()
    {
        await OpenFixtureAsync("everything", "write");
        await ShouldBeReadyAsync();
        await SwitchToBlocksAsync();
        // A field made read-only by something else must stay so.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").EvaluateAsync("field => { field.readOnly = true; }");

        await Button(Header("callout-warning"), "Move up").ClickAsync();
        await WaitForChangesAsync(1);
        await SettleAsync();

        (await ReadOnlyFieldsAsync()).ShouldBe(["Button label"]);
        await Button(Header("callout-warning"), "Remove").ClickAsync();
        await WaitForChangesAsync(2);
        await SettleAsync();
        (await ReadOnlyFieldsAsync()).ShouldBe(["Button label"]);
        await CtaBody.FillAsync("Typed in blocks mode.");
        Block(await SaveAsync(Page), "cta").ShouldContain("Typed in blocks mode.");
    }

    [Theory]
    // The release is never answered; or the switch is made while the render of the rearrangement is still held.
    [InlineData("unanswered")]
    [InlineData("held")]
    [InlineData("held-and-back")]
    public async Task ASwitchWhileTheEditorIsFrozen_LeavesNothingReadOnly(string how)
    {
        await Page.AddInitScriptAsync(HeldRenderTests.HoldScript);
        await OpenFixtureAsync("everything", "write");
        await ShouldBeReadyAsync();
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").EvaluateAsync("field => { field.readOnly = true; }");
        if (how == "unanswered")
        {
            await Page.EvaluateAsync("() => { document.querySelector('.doc-editor').richContentFreeze.ask = () => new Promise(() => {}); }");
        }
        else
        {
            await Page.EvaluateAsync("() => window.hold()");
        }

        await Button(Card("prose", "code").First, "Move up").ClickAsync();
        // The call to action's fields are the same elements in both modes, and are frozen now.
        (await CtaBody.EvaluateAsync<bool>("field => field.readOnly")).ShouldBeTrue();
        await Page.Locator("#mode-blocks").ClickAsync();
        if (how == "held-and-back")
        {
            await Page.Locator("#mode-write").ClickAsync();
        }

        if (how != "unanswered")
        {
            await Page.EvaluateAsync("() => window.resume()");
        }

        if (how == "held-and-back")
        {
            await ShouldBeReadyAsync();
        }
        else
        {
            await Expect(Page.GetByRole(AriaRole.Toolbar)).ToHaveCountAsync(0);
        }

        await SettleAsync();
        (await ReadOnlyFieldsAsync()).ShouldBe(["Button label"]);
        await CtaBody.FillAsync("Typed after the switch.");

        // And back in Write mode, everything can be typed into.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").EvaluateAsync("field => { field.readOnly = false; }");
        await SwitchToWriteAsync();
        await ShouldBeEditableAsync();
        await PlaceCaretAsync(Surface("prose").Locator(":scope > h2"));
        await Page.Keyboard.TypeAsync("!");
        var saved = await SaveAsync(Page);
        Block(saved, "cta").ShouldContain("Typed after the switch.");
        TextOf(Nodes(saved, "prose")[1]).ShouldBe("A heading!");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASwitchMadeWhileDisconnected_LeavesNothingReadOnly(bool strictCsp)
    {
        await OpenFixtureAsync("everything", "write", strictCsp);
        await ShouldBeReadyAsync();
        await Page.EvaluateAsync("() => { document.querySelector('.doc-editor').richContentFreeze.ask = () => new Promise(() => {}); }");
        await Page.Locator("#mode-blocks-later").ClickAsync();
        await Button(Card("prose", "code").First, "Move up").ClickAsync();
        await WaitForChangesAsync(1);
        (await CtaBody.EvaluateAsync<bool>("field => field.readOnly")).ShouldBeTrue();

        var outage = await Watch.DropConnectionAsync(Page);
        // The switch is due while the connection is down.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await outage.RestoreAsync();
        await Expect(Page.GetByRole(AriaRole.Toolbar)).ToHaveCountAsync(0);
        await SettleAsync();

        (await ReadOnlyFieldsAsync()).ShouldBeEmpty();
        await CtaBody.FillAsync("Typed after reconnecting.");
        await SwitchToWriteAsync();
        await ShouldBeEditableAsync();
        await Button(Card("prose", "code").First, "Move down").ClickAsync();
        await WaitForChangesAsync(3);
        await ShouldBeEditableAsync();
        Block(await SaveAsync(Page), "cta").ShouldContain("Typed after reconnecting.");
    }
}
