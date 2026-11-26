using System.Globalization;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static Microsoft.Playwright.Assertions;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>The editor's styles: readable in light and dark hosts, and open to a host's own colours.</summary>
public sealed class ThemeTests(SampleHost host) : BrowserTest(host)
{
    // The page of a host that states its two colours and nothing else, not even a color-scheme: the editor has only
    // those to work from.
    private const string ThemeScript = """
        ([background, text]) => {
            for (const element of [document.documentElement, document.body]) {
                element.style.backgroundColor = background;
                element.style.color = text;
            }
        }
        """;

    // The lowest contrast (WCAG) of the text of the matching elements against what is behind it, as "ratio|detail".
    // Colours are composited on a canvas, which understands whatever form the browser computes them in.
    private const string ContrastScript = """
        selector => {
            const canvas = document.createElement('canvas');
            canvas.width = canvas.height = 1;
            const context = canvas.getContext('2d', { willReadFrequently: true });
            const paint = colour => {
                context.fillStyle = '#010203';
                context.fillStyle = colour;
                if (context.fillStyle === '#010203') {
                    throw new Error('The canvas does not understand ' + colour);
                }

                context.fillRect(0, 0, 1, 1);
            };
            const pixel = () => [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
            const luminance = rgb => rgb
                .map(value => value / 255)
                .map(value => value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4)
                .reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0);

            let lowest = null;
            for (const element of document.querySelectorAll(selector)) {
                if (!element.checkVisibility()) {
                    continue;
                }

                const chain = [];
                for (let each = element; each; each = each.parentElement) {
                    chain.unshift(each);
                }

                context.fillStyle = '#ffffff';
                context.fillRect(0, 0, 1, 1);
                for (const each of chain) {
                    paint(getComputedStyle(each).backgroundColor);
                }

                const behind = pixel();
                paint(getComputedStyle(element).color);
                const [light, dark] = [luminance(pixel()), luminance(behind)].sort((a, b) => b - a);
                const ratio = (light + 0.05) / (dark + 0.05);
                if (lowest === null || ratio < lowest.ratio) {
                    lowest = { ratio, detail: element.outerHTML.slice(0, 120) + ' on rgb(' + behind + ')' };
                }
            }

            return lowest === null ? null : lowest.ratio.toFixed(2) + '|' + lowest.detail;
        }
        """;

    private static readonly (string Name, string Background, string Text)[] Themes =
        [("dark", "#121212", "#e6e6e6"), ("light", "#ffffff", "#1f1f1f")];

    [Theory]
    [InlineData("blocks")]
    [InlineData("write")]
    public async Task Theme_DarkAndLightHost_TextContrastAtLeast4_5(string mode)
    {
        await OpenFixtureAsync("unknown", mode);
        // An error to read.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").FillAsync("");
        await Expect(Page.Locator("[data-block-id=cta] .errors li").First).ToBeVisibleAsync();
        await Page.Locator(".insert-menu > summary").First.ClickAsync();

        List<(string Name, string Selector)> texts =
        [
            ("insert summary", ".insert-menu > summary"),
            ("button", ".doc-editor button:not(:disabled)"),
            ("error", ".doc-editor .errors li"),
            ("unsupported block", ".doc-editor .preserved"),
            ("field", ".doc-editor :is(input[type=text], textarea, select):not(:disabled)"),
        ];
        if (mode == "write")
        {
            // A removal that is asking, and then the link box with a refused address. In this order, since going to a
            // card closes the box.
            await Page.Locator("[data-rt-card=locked]").First.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            await Expect(Page.Locator("[data-rt-confirm]")).ToBeVisibleAsync();
            await Page.Locator("[data-block-id=mixed] [data-rt-surface] p").First.ClickAsync();
            await Page.Locator("[data-rt-toolbar]").GetByRole(AriaRole.Button, new() { Name = "Link" }).ClickAsync();
            await Page.GetByLabel("Link address").FillAsync("javascript:alert(1)");
            await Page.GetByRole(AriaRole.Button, new() { Name = "Apply link" }).ClickAsync();
            await Expect(Page.Locator(".rt-link-box .error")).ToBeVisibleAsync();

            texts.AddRange(
            [
                ("hint", ".doc-editor .hint"),
                ("locked card text", ".rt-card[data-rt-card=locked] .rt-locked-text"),
                ("card title", ".rt-card-header strong"),
                ("confirmation", ".rt-confirm > span"),
                ("link box", ".rt-link-box label"),
                ("link refusal", ".rt-link-box .error"),
            ]);
        }

        var failures = new List<string>();
        foreach (var (theme, background, text) in Themes)
        {
            await Page.EvaluateAsync(ThemeScript, new[] { background, text });
            foreach (var (name, selector) in texts)
            {
                var found = await Page.EvaluateAsync<string?>(ContrastScript, selector);
                if (found is null)
                {
                    failures.Add($"{theme} {name}: nothing visible matches {selector}");
                }
                else if (double.Parse(found.Split('|')[0], CultureInfo.InvariantCulture) < 4.5)
                {
                    failures.Add($"{theme} {name}: {found}");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    // The browser's own link colours do not follow the page's, and are close to invisible on a dark one.
    [Fact]
    public async Task Theme_DarkAndLightHost_FormattedTextInASurface_ContrastAtLeast4_5()
    {
        await OpenFixtureAsync("everything", "write");

        var failures = new List<string>();
        foreach (var (theme, background, text) in Themes)
        {
            await Page.EvaluateAsync(ThemeScript, new[] { background, text });
            foreach (var selector in new[]
            {
                "[data-rt-surface] a[href]",
                "[data-rt-surface] font[face='rt-code']",
                "[data-rt-surface] blockquote p",
                ".rt-table td[data-rt-header] [data-rt-surface]",
                ".rt-mode-switch button",
            })
            {
                var found = await Page.EvaluateAsync<string?>(ContrastScript, selector);
                if (found is null || double.Parse(found.Split('|')[0], CultureInfo.InvariantCulture) < 4.5)
                {
                    failures.Add($"{theme} {selector}: {found ?? "nothing visible matches"}");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Theme_CustomProperties_SetByTheHost_AreUsed()
    {
        await OpenFixtureAsync("unknown", "write");
        await Page.EvaluateAsync("""
            () => {
                const style = document.body.style;
                style.setProperty('--rt-border', 'rgb(1, 2, 3)');
                style.setProperty('--rt-muted', 'rgb(4, 5, 6)');
                style.setProperty('--rt-accent', 'rgb(7, 8, 9)');
                style.setProperty('--rt-error', 'rgb(10, 11, 12)');
                style.setProperty('--rt-surface', 'rgb(13, 14, 15)');
                style.setProperty('--rt-button-bg', 'rgb(16, 17, 18)');
                style.setProperty('--rt-button-text', 'rgb(19, 20, 21)');
            }
            """);
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").FillAsync("");
        await Expect(Page.Locator("[data-block-id=cta] .errors li").First).ToBeVisibleAsync();

        await Expect(Page.Locator(".rt-card").First).ToHaveCSSAsync("border-top-color", "rgb(1, 2, 3)");
        await Expect(Page.Locator(".doc-editor .hint").First).ToHaveCSSAsync("color", "rgb(4, 5, 6)");
        await Expect(Page.Locator(".insert-menu > summary").First).ToHaveCSSAsync("color", "rgb(7, 8, 9)");
        await Expect(Page.Locator("[data-block-id=cta] .errors li").First).ToHaveCSSAsync("color", "rgb(10, 11, 12)");
        await Expect(Page.Locator(".rt-toolbar-area")).ToHaveCSSAsync("background-color", "rgb(13, 14, 15)");
        var button = Page.Locator("[data-block-id=cta] > header button:not(:disabled)").First;
        await Expect(button).ToHaveCSSAsync("background-color", "rgb(16, 17, 18)");
        await Expect(button).ToHaveCSSAsync("color", "rgb(19, 20, 21)");
    }

    // A host's own rules for buttons and fields, such as a framework's classes in its media picker or an extension
    // block's editor, must not lose to the editor's defaults.
    [Fact]
    public async Task Theme_AHostsOwnControlStyles_WinOverTheEditorsDefaults()
    {
        await OpenFixtureAsync("everything", "write");

        var colour = await Page.EvaluateAsync<string>("""
            () => {
                const sheet = new CSSStyleSheet();
                sheet.replaceSync('.host-button { color: rgb(200, 0, 0); }');
                document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
                const button = document.querySelector('[data-block-id=cta] > header button:not(:disabled)');
                button.classList.add('host-button');
                return getComputedStyle(button).color;
            }
            """);

        colour.ShouldBe("rgb(200, 0, 0)");
    }

    [Fact]
    public async Task Toolbar_StaysInView_WhileALongDocumentScrolls()
    {
        await Page.SetViewportSizeAsync(1280, 700);
        await OpenFixtureAsync("post", "write");
        var toolbar = Page.Locator("[data-rt-toolbar]");

        await Page.Locator("[data-block-id=part-two] [data-rt-surface] p").Last.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("() => window.scrollY > 700");

        var top = await toolbar.EvaluateAsync<double>("element => element.getBoundingClientRect().top");
        top.ShouldBeInRange(0, 40);
    }

    // A surface with nothing in it has no text to show where it is.
    [Fact]
    public async Task AnEmptyPlaceToType_IsOutlined_AndTakesNoOtherRoomWhenTypedIn()
    {
        await OpenFixtureAsync("everything", "write");
        // Between the two code and image cards of the first block there is nowhere to type but an empty place.
        var empty = Page.Locator("[data-block-id=prose] .rt-surface.rt-empty").First;

        await Expect(empty).ToHaveCSSAsync("outline-style", "dashed");
        var filled = Page.Locator("[data-block-id=prose] .rt-surface:not(.rt-empty)").First;
        await Expect(filled).ToHaveCSSAsync("outline-style", "none");
        var before = await empty.EvaluateAsync<double>("element => element.getBoundingClientRect().height");
        before.ShouldBeGreaterThanOrEqualTo(20);

        await empty.EvaluateAsync("element => { element.marked = true; }");
        await empty.ClickAsync();
        await Page.Keyboard.TypeAsync("x");

        var typed = Page.Locator("[data-block-id=prose] [data-rt-surface]").Filter(new() { HasTextString = "x" });
        await Expect(Page.Locator("#changes")).Not.ToHaveTextAsync("0");
        (await Page.EvaluateAsync<bool>("() => document.activeElement.marked === true")).ShouldBeTrue("the surface typed in is kept");
        (await Page.EvaluateAsync<double>("() => document.activeElement.getBoundingClientRect().height")).ShouldBe(before, 1);
        await Expect(typed.First).ToBeAttachedAsync();
    }
}
