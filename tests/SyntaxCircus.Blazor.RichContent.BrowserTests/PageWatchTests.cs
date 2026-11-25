using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using Xunit.Sdk;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// Shows that the failure detection every browser test relies on does detect. Each test causes a failure and
/// takes it from the watch, so that the test itself can pass.
/// </summary>
public class PageWatchTests(SampleHost host) : BrowserTest(host)
{
    private const string LogError = "() => console.error('deliberate console error')";
    private const string LoadForeignImage = "() => { new Image().src = 'https://blocked.invalid/image.png'; }";
    private const string AddInlineScript =
        "() => { const script = document.createElement('script'); script.textContent = 'window.ran = true'; document.head.appendChild(script); }";
    private const string ThrowLater = "() => { setTimeout(() => { throw new Error('deliberate page error'); }, 0); }";

    [Fact]
    public async Task ConsoleError_IsRecorded()
    {
        await OpenFixtureAsync("empty");

        await Page.EvaluateAsync(LogError);

        var failure = (await Watch.TakeFailuresAsync()).ShouldHaveSingleItem();
        failure.Kind.ShouldBe(PageFailureKind.ConsoleError);
        failure.Detail.ShouldContain("deliberate console error");
    }

    [Fact]
    public async Task UncaughtError_IsRecorded()
    {
        await OpenFixtureAsync("empty");

        await Page.EvaluateAsync(ThrowLater);

        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Kind == PageFailureKind.PageError && f.Detail.Contains("deliberate page error"));
    }

    [Theory]
    [InlineData(LoadForeignImage, "img-src")]
    [InlineData(AddInlineScript, "script-src")]
    public async Task CspViolation_IsRecorded(string violate, string directive)
    {
        await OpenFixtureAsync("empty", strictCsp: true);

        await Page.EvaluateAsync(violate);

        // The browser may log the violation as a console error too; the violation itself must be recorded.
        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Kind == PageFailureKind.CspViolation && f.Detail.StartsWith(directive));
    }

    [Fact]
    public async Task MessageOverTheDefaultLimit_LosesTheConnection_AndIsRecorded()
    {
        await OpenFixtureAsync("everything");

        // Blocks mode sends a field's whole value in one message.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Body").FillAsync(new string('x', 40 * 1024));
        var failures = new List<PageFailure>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!failures.Any(f => f.Kind == PageFailureKind.ConnectionLost) && DateTime.UtcNow < deadline)
        {
            failures.AddRange(await Watch.TakeFailuresAsync());
        }

        failures.ShouldContain(f => f.Kind == PageFailureKind.ConnectionLost);

        // Blazor goes on to reconnect and may log more; that is part of the failure this test caused.
        await Page.CloseAsync();
        await Watch.TakeFailuresAsync();
    }

    [Fact]
    public async Task LeavingThePage_IsNotALostConnection()
    {
        await OpenFixtureAsync("empty");

        await OpenFixtureAsync("everything");
        await Page.ReloadAsync();
        await Expect(Page.Locator("#fixture")).ToHaveAttributeAsync("data-interactive", "true");

        (await Watch.TakeFailuresAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(LogError, false, PageFailureKind.ConsoleError)]
    [InlineData(LoadForeignImage, true, PageFailureKind.CspViolation)]
    [InlineData(ThrowLater, false, PageFailureKind.PageError)]
    public async Task ATest_FailsWhenItEnds_IfThePageReportedAFailure(string cause, bool strictCsp, PageFailureKind kind)
    {
        // A second test instance, driven by hand the way xunit drives one.
        var probe = new Probe(Host);
        await probe.InitializeAsync();
        await probe.RunAsync(cause, strictCsp);

        var thrown = await Should.ThrowAsync<FailException>(() => probe.DisposeAsync().AsTask());

        thrown.Message.ShouldContain(kind.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outage_ToleratesTheDrop_StaysDown_AndRestoresTheSameCircuit(bool strictCsp)
    {
        await OpenFixtureAsync("everything", strictCsp: strictCsp);
        await Page.Locator("[data-block-id=cta]").GetByLabel("Button label").FillAsync("Before the outage");
        await SaveAsync(Page);
        var changes = await ChangeCountAsync(Page);

        var outage = await Watch.DropConnectionAsync(Page);
        await Page.EvaluateAsync(LogError);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        (await PageWatch.OpenSocketsAsync(Page)).ShouldBe(0);
        await outage.RestoreAsync();

        (await SaveAsync(Page)).ShouldContain("Before the outage");
        (await ChangeCountAsync(Page)).ShouldBe(changes);
        (await Watch.TakeFailuresAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Outage_StillRecordsCspViolationsAndUncaughtErrors()
    {
        await OpenFixtureAsync("empty", strictCsp: true);

        var outage = await Watch.DropConnectionAsync(Page);
        await Page.EvaluateAsync(LoadForeignImage);
        await Page.EvaluateAsync(ThrowLater);
        await outage.RestoreAsync();

        var kinds = (await Watch.TakeFailuresAsync()).Select(f => f.Kind).Distinct();
        kinds.ShouldBe([PageFailureKind.CspViolation, PageFailureKind.PageError], ignoreOrder: true);
    }

    [Fact]
    public async Task AfterAnOutage_FailuresCountAgain()
    {
        await OpenFixtureAsync("empty");
        var outage = await Watch.DropConnectionAsync(Page);
        await outage.RestoreAsync();

        await Page.EvaluateAsync(LogError);

        (await Watch.TakeFailuresAsync()).ShouldHaveSingleItem().Kind.ShouldBe(PageFailureKind.ConsoleError);
    }

    private sealed class Probe(SampleHost host) : BrowserTest(host)
    {
        public async Task RunAsync(string cause, bool strictCsp)
        {
            await OpenFixtureAsync("empty", strictCsp: strictCsp);
            await Page.EvaluateAsync(cause);
        }
    }
}
