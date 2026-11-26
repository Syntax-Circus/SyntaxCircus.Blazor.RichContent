using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using Xunit.Sdk;
using static Microsoft.Playwright.Assertions;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// Shows that the failure detection every browser test relies on does detect. Each test causes a failure and
/// takes it from the watch, so that the test itself can pass. <see cref="ServerFailureTests"/> does the same for
/// the server's side.
/// </summary>
public class PageWatchTests(SampleHost host) : BrowserTest(host)
{
    private const string LogError = "() => console.error('deliberate console error')";
    private const string LoadForeignImage = "() => { new Image().src = 'https://blocked.invalid/image.png'; }";
    private const string AddInlineScript =
        "() => { const script = document.createElement('script'); script.textContent = 'window.ran = true'; document.head.appendChild(script); }";
    private const string ThrowLater = "() => { setTimeout(() => { throw new Error('deliberate page error'); }, 0); }";
    private const string RejectUnhandled = "() => { Promise.reject(new Error('deliberate rejection')); }";
    private const string CloseSockets = "() => window.__pageWatch.closeSockets()";

    [Fact]
    public async Task ConsoleError_IsRecorded()
    {
        await OpenFixtureAsync("empty");

        await Page.EvaluateAsync(LogError);

        var failure = (await Watch.TakeFailuresAsync()).ShouldHaveSingleItem();
        failure.Kind.ShouldBe(PageFailureKind.ConsoleError);
        failure.Detail.ShouldContain("deliberate console error");
    }

    [Theory]
    [InlineData(ThrowLater, "deliberate page error")]
    [InlineData(RejectUnhandled, "deliberate rejection")]
    public async Task UncaughtErrorOrRejection_IsRecorded(string cause, string message)
    {
        await OpenFixtureAsync("empty");

        await Page.EvaluateAsync(cause);

        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Kind == PageFailureKind.PageError && f.Detail.Contains(message));
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
    public async Task CspViolation_WhileThePageLoads_IsRecorded()
    {
        await using var server = SampleServer.Start(strictCsp: true, faults: true);

        // The image is in the HTML the server sends, so it is blocked before any script of the page has run.
        await Page.GotoAsync(new Uri(server.Address, "faults?foreignImage=true").AbsoluteUri);
        await Expect(Page.Locator("#faults")).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });

        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Kind == PageFailureKind.CspViolation && f.Detail.StartsWith("img-src"));

        // Leave before the server goes away, or its going would look like a lost connection.
        await Page.GotoAsync("about:blank");
        await Watch.TakeFailuresAsync();
    }

    [Fact]
    public async Task MessageOverTheDefaultLimit_LosesTheConnection_AndIsRecorded()
    {
        await OpenFixtureAsync("everything");

        // Blocks mode sends a field's whole value in one message.
        await Page.Locator("[data-block-id=cta]").GetByLabel("Body").FillAsync(new string('x', 40 * 1024));

        await ShouldRecordALostConnectionAsync();
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

    [Fact]
    public async Task BeforeUnloadOnAPageThatStays_DoesNotHideALostConnection()
    {
        await OpenFixtureAsync("empty");
        await Page.RouteAsync("**/nothing-to-show", route => route.FulfillAsync(new() { Status = 204 }));

        // A navigation answered with no content starts to unload the page and then leaves it where it is. A
        // download or a mailto: link does the same.
        await Page.EvaluateAsync("() => { window.dispatchEvent(new Event('beforeunload')); location.href = '/nothing-to-show'; }");
        await Page.EvaluateAsync(CloseSockets);

        await ShouldRecordALostConnectionAsync();
    }

    [Fact]
    public async Task ALossWellAfterBeforeUnload_IsRecorded_EvenIfThePageThenReloads()
    {
        await OpenFixtureAsync("empty");
        await Page.EvaluateAsync("() => { window.dispatchEvent(new Event('beforeunload')); }");
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        // What Blazor does when its circuit is gone: the connection drops, and then it reloads the page.
        await Page.EvaluateAsync(CloseSockets);
        await Page.ReloadAsync();
        await Expect(Page.Locator("#fixture")).ToHaveAttributeAsync("data-interactive", "true");

        (await Watch.TakeFailuresAsync()).ShouldContain(f => f.Kind == PageFailureKind.ConnectionLost);
    }

    [Fact]
    public async Task ACloseThatComesLateInANavigation_IsNotALostConnection()
    {
        await OpenFixtureAsync("empty");
        // A page that is slow to answer, so that the one being left stays alive well after beforeunload.
        await Page.RouteAsync("**/slow-page", async route =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><title>there</title>" });
        });

        // What Firefox does under load: it tells the page of the close well after beforeunload.
        // The close is arranged beforehand: nothing can be asked of a page while it is being left.
        await Page.EvaluateAsync("() => { setTimeout(() => window.__pageWatch.closeSockets(), 1500); location.href = '/slow-page'; }");
        await Expect(Page).ToHaveTitleAsync("there", new() { Timeout = 30_000 });

        // Only the close is in question: WebKit also reports Blazor's attempts to reconnect from a page that is going.
        (await Watch.TakeFailuresAsync()).ShouldNotContain(f => f.Kind == PageFailureKind.ConnectionLost);
    }

    [Fact]
    public async Task ALossLongAfterABeforeUnloadThatWasIgnored_IsRecordedAtTheNextLook()
    {
        await OpenFixtureAsync("empty");
        await Page.EvaluateAsync("() => { window.dispatchEvent(new Event('beforeunload')); }");
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        await Page.EvaluateAsync(CloseSockets);

        await ShouldRecordALostConnectionAsync();
    }

    [Fact]
    public async Task APageShownAgainAfterPageHide_IsWatchedAgain()
    {
        await OpenFixtureAsync("empty");

        // What the back/forward cache does to a page: hidden, then shown again.
        await Page.EvaluateAsync(
            """
            () => {
                window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
                window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
            }
            """);
        await Page.EvaluateAsync(CloseSockets);

        await ShouldRecordALostConnectionAsync();
    }

    [Theory]
    [InlineData(LogError, false, PageFailureKind.ConsoleError)]
    [InlineData(LoadForeignImage, true, PageFailureKind.CspViolation)]
    [InlineData(ThrowLater, false, PageFailureKind.PageError)]
    public async Task ATest_FailsWhenItEnds_IfThePageReportedAFailure(string cause, bool strictCsp, PageFailureKind kind)
    {
        await using var server = SampleServer.Start(strictCsp);
        var probe = await ProbeTest.StartAsync(Host, server);
        await probe.OpenAsync("fixture/empty");
        await probe.CurrentPage.EvaluateAsync(cause);

        var thrown = await Should.ThrowAsync<FailException>(probe.EndAsync);

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

    private async Task ShouldRecordALostConnectionAsync()
    {
        var failures = new List<PageFailure>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!failures.Any(f => f.Kind == PageFailureKind.ConnectionLost) && DateTime.UtcNow < deadline)
        {
            failures.AddRange(await Watch.TakeFailuresAsync());
        }

        failures.ShouldContain(f => f.Kind == PageFailureKind.ConnectionLost);

        // Blazor goes on to reconnect and may log more; that is part of the failure this test caused.
        await Page.CloseAsync();
        await Watch.TakeFailuresAsync();
    }
}
