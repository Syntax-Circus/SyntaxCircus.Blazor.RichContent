using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// What happens when the script throws while drawing a surface. The server logs that as an error on purpose, so these
/// tests have a server of their own and take its failures from it.
/// </summary>
public class SurfaceDrawFailureTests(SampleHost host) : SurfaceTest(host)
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

    // Makes one step of drawing the first surface throw, until window.heal() is called.
    private const string BreakDrawing = """
        (element, step) => {
            const fail = () => { throw new Error(`deliberate ${step} failure`); };
            if (step === 'building') {
                element.replaceChildren = fail;
                window.heal = () => delete element.replaceChildren;
            } else if (step === 'reading') {
                const walker = document.createTreeWalker;
                document.createTreeWalker = fail;
                window.heal = () => { document.createTreeWalker = walker; };
            } else {
                const transport = element.richContentSurface.transport;
                const reset = transport.reset;
                transport.reset = (generation, entries) => (generation === null ? reset(generation, entries) : fail());
                window.heal = () => { transport.reset = reset; };
            }
        }
        """;

    private const string State = """
        element => [
            element.contentEditable,
            String(element.richContentSurface.generation),
            String(element.hasAttribute('data-rt-ready')),
        ]
        """;

    private Task OpenAsync() => FixturePage.OpenAsync(Page, _server.Address, "everything", "write");

    private async Task<int> WaitForServerFailuresAsync(string containing)
    {
        var found = 0;
        for (var attempt = 0; attempt < 250 && found == 0; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            found = _server.Log.TakeFailures().Count(failure => failure.ToString().Contains(containing, StringComparison.Ordinal));
        }

        return found;
    }

    [Theory]
    [InlineData("building")]
    [InlineData("reading")]
    [InlineData("resetting")]
    public async Task ADrawThatThrows_IsNeverConfirmed_TakesNoTyping_AndCanBeRetried(string step)
    {
        await OpenAsync();
        await TapAsync();
        var surface = Surface("prose");
        var first = surface.Locator(":scope > p").First;
        await surface.EvaluateAsync(BreakDrawing, step);

        await Page.Locator("#replace-other").ClickAsync();

        (await WaitForServerFailuresAsync($"deliberate {step} failure")).ShouldBeGreaterThan(0);
        await ShouldBeUnusableAsync();

        // The next parameters send the redraw again. It fails again, and must not be taken as drawn.
        await Page.Locator("#replace-same").ClickAsync();
        (await WaitForServerFailuresAsync($"deliberate {step} failure")).ShouldBeGreaterThan(0);
        await ShouldBeUnusableAsync();

        await Page.EvaluateAsync("() => window.heal()");
        await Page.Locator("#replace-same").ClickAsync();

        await Expect(surface).ToHaveAttributeAsync("contenteditable", "true");
        await Expect(first).ToHaveTextAsync(Fixtures.ReplacementText);
        await PlaceCaretAsync(first);
        await Page.Keyboard.TypeAsync(" Kept.");
        await WaitUntilAcknowledgedAsync();
        var saved = await SaveAsync(Page);
        TextOf(Nodes(saved, "prose")[0]).ShouldBe(Fixtures.ReplacementText + " Kept.");
        _server.Log.TakeFailures();

        async Task ShouldBeUnusableAsync()
        {
            (await surface.EvaluateAsync<string[]>(State)).ShouldBe(["false", "null", "false"]);
            var shown = await surface.TextContentAsync();
            // Forced: it cannot be typed into, which is the point.
            await surface.ClickAsync(new() { Force = true });
            await Page.Keyboard.TypeAsync("lost?");
            (await surface.TextContentAsync()).ShouldBe(shown);
            (await SentPatchesAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task ADrawThatThrowsAfterAComposition_LeavesTheSurfaceUnusable_AndUnconfirmed()
    {
        await OpenAsync();
        var surface = Surface("prose");
        await surface.EvaluateAsync("element => element.dispatchEvent(new CompositionEvent('compositionstart'))");
        await Page.Locator("#replace-other").ClickAsync();
        await Page.WaitForFunctionAsync("() => !!document.querySelector('[data-rt-surface]').richContentSurface.pending");
        await surface.EvaluateAsync(BreakDrawing, "building");

        await surface.EvaluateAsync("element => element.dispatchEvent(new CompositionEvent('compositionend'))");

        // Nothing was waiting for this draw, so it fails in the page.
        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Detail.Contains("deliberate building failure"));
        (await surface.EvaluateAsync<string[]>(State)).ShouldBe(["false", "null", "false"]);

        await Page.EvaluateAsync("() => window.heal()");
        await Page.Locator("#replace-same").ClickAsync();
        await Expect(surface.Locator(":scope > p").First).ToHaveTextAsync(Fixtures.ReplacementText);
        await Expect(surface).ToHaveAttributeAsync("contenteditable", "true");
        _server.Log.TakeFailures();
    }

    [Fact]
    public async Task AFirstDrawOfACodeCardThatThrows_ShowsTheNotice_AndLeavesNothingToTypeIn()
    {
        // Broken before the cards exist.
        await Page.AddInitScriptAsync("""
            const value = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value');
            Object.defineProperty(HTMLTextAreaElement.prototype, 'value', {
                ...value,
                set(text) {
                    if (this.hasAttribute('data-rt-code')) {
                        window.broken = [...(window.broken ?? []), this];
                        throw new Error('deliberate first code draw failure');
                    }

                    value.set.call(this, text);
                },
            });
            """);

        await Page.GotoAsync(new Uri(_server.Address, "fixture/everything?mode=write").AbsoluteUri);

        await Expect(Page.Locator(".rt-surface-failed").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("textarea[data-rt-code]")).ToHaveCountAsync(0);
        (await WaitForServerFailuresAsync("deliberate first code draw failure")).ShouldBeGreaterThan(0);
        (await Page.EvaluateAsync<bool>("() => window.broken.length > 0 && window.broken.every(e => e.readOnly)")).ShouldBeTrue();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        _server.Log.TakeFailures();
    }

    [Fact]
    public async Task AFirstDrawThatThrows_ShowsTheNotice_AndLeavesNothingEditable()
    {
        // Broken before the surfaces exist.
        await Page.AddInitScriptAsync("""
            const replaceChildren = Element.prototype.replaceChildren;
            Element.prototype.replaceChildren = function (...nodes) {
                if (this.hasAttribute('data-rt-surface')) {
                    window.broken = [...(window.broken ?? []), this];
                    throw new Error('deliberate first draw failure');
                }

                return replaceChildren.apply(this, nodes);
            };
            """);

        await Page.GotoAsync(new Uri(_server.Address, "fixture/everything?mode=write").AbsoluteUri);

        await Expect(Page.Locator(".rt-surface-failed").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("[data-rt-surface]")).ToHaveCountAsync(0);
        (await WaitForServerFailuresAsync("deliberate first draw failure")).ShouldBeGreaterThan(0);
        // The elements the script had in hand were not left editable either.
        (await Page.EvaluateAsync<bool>("() => window.broken.every(e => e.contentEditable !== 'true')")).ShouldBeTrue();
        (await SaveAsync(Page)).ShouldBe(Original("everything"));
        _server.Log.TakeFailures();
    }
}
