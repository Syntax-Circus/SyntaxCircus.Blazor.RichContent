using System.Globalization;
using static Microsoft.Playwright.Assertions;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>Helpers for the sample's <c>/fixture/{name}</c> page.</summary>
public static class FixturePage
{
    // Longer than Playwright's default, for a cold start and for saving a very large document.
    private const float TimeoutMilliseconds = 30_000;

    public static async Task OpenAsync(IPage page, Uri address, string name, string mode)
    {
        await page.GotoAsync(new Uri(address, $"fixture/{name}?mode={mode}").AbsoluteUri);

        // The prerendered page looks ready before it can respond to anything.
        await Expect(page.Locator("#fixture")).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = TimeoutMilliseconds });
        (await PageWatch.OpenSocketsAsync(page)).ShouldBe(1, "the lost-circuit check watches the Blazor WebSocket");
    }

    /// <summary>
    /// Clicks Save and returns the serialized document the host held when the click was handled. Everything the
    /// page sent before the click has been handled by then.
    /// </summary>
    public static async Task<string> SaveAsync(IPage page)
    {
        var saved = page.Locator("#saved");
        var before = await SaveNumberAsync(page);
        await page.Locator("#save").ClickAsync();
        await WaitForSaveAsync(page, before);
        return await saved.TextContentAsync() ?? "";
    }

    /// <summary>How many saves have shown. Use with <see cref="WaitForSaveAsync"/> when a test clicks Save itself.</summary>
    public static async Task<int> SaveNumberAsync(IPage page) =>
        int.Parse(await page.Locator("#saved").GetAttributeAsync("data-save") ?? "", CultureInfo.InvariantCulture);

    public static Task WaitForSaveAsync(IPage page, int previous) =>
        Expect(page.Locator("#saved")).ToHaveAttributeAsync(
            "data-save",
            (previous + 1).ToString(CultureInfo.InvariantCulture),
            new() { Timeout = TimeoutMilliseconds });

    /// <summary>
    /// How many times the editor has raised <c>DocumentChanged</c>. This reads the page as it is now, so to show
    /// that something raised no change, call <see cref="SaveAsync"/> first.
    /// </summary>
    public static async Task<int> ChangeCountAsync(IPage page) =>
        int.Parse(await page.Locator("#changes").TextContentAsync() ?? "", CultureInfo.InvariantCulture);

    /// <summary>The validation error codes the editor last reported.</summary>
    public static Task<IReadOnlyList<string>> ErrorCodesAsync(IPage page) => page.Locator("#errors li").AllTextContentsAsync();
}
