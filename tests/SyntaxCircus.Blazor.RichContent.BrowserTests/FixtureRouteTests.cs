using RichContent.Sample;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using SyntaxCircus.RichContent;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

public class FixtureRouteTests(SampleHost host) : BrowserTest(host)
{
    [Theory]
    [InlineData("everything")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("post")]
    public async Task Blocks_Fixture_SavesIdenticalJson(string name)
    {
        await OpenFixtureAsync(name);

        var saved = await SaveAsync(Page);

        saved.ShouldBe(Json(Fixtures.Get(name)!));
        (await ChangeCountAsync(Page)).ShouldBe(0);
        (await ErrorCodesAsync(Page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteMode_IsAccepted()
    {
        await OpenFixtureAsync("everything", "write");

        (await SaveAsync(Page)).ShouldBe(Json(Fixtures.Get("everything")!));
    }

    [Fact]
    public async Task EverySave_ShowsTheDocumentAgain()
    {
        await OpenFixtureAsync("empty");
        var first = await SaveAsync(Page);
        var number = await SaveNumberAsync(Page);

        (await SaveAsync(Page)).ShouldBe(first);

        (await SaveNumberAsync(Page)).ShouldBe(number + 1);
    }

    [Fact]
    public async Task AuthorEdit_IsCounted_SavedAndItsErrorsAreListed()
    {
        await OpenFixtureAsync("everything");

        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").FillAsync("");
        var saved = await SaveAsync(Page);

        (await ChangeCountAsync(Page)).ShouldBeGreaterThan(0);
        var cta = ContentDocumentSerializer.Deserialize(saved).Document!.Blocks.OfType<CtaBlock>().ShouldHaveSingleItem();
        cta.Label.ShouldBe("");
        await Expect(Page.Locator("#errors li")).ToHaveTextAsync(["cta-label-required"]);
    }

    [Fact]
    public async Task ReplaceSame_KeepsTheJson_AndRaisesNoChange()
    {
        await OpenFixtureAsync("everything");

        await Page.Locator("#replace-same").ClickAsync();

        (await SaveAsync(Page)).ShouldBe(Json(Fixtures.Get("everything")!));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    [Fact]
    public async Task ReplaceOther_SuppliesDifferentContent_AndRaisesNoChange()
    {
        await OpenFixtureAsync("everything");

        await Page.Locator("#replace-other").ClickAsync();
        var saved = await SaveAsync(Page);

        saved.ShouldBe(Json(Fixtures.Replaced(Fixtures.Get("everything")!)));
        saved.ShouldNotBe(Json(Fixtures.Get("everything")!));
        (await ChangeCountAsync(Page)).ShouldBe(0);
    }

    private static string Json(ContentDocument document) => ContentDocumentSerializer.Serialize(document);
}
