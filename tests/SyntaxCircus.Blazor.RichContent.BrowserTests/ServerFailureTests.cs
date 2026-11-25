using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using Xunit.Sdk;
using static Microsoft.Playwright.Assertions;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// Shows that a failure on the server fails the test, including the ones the browser never hears about. Every
/// deliberate failure happens on a server of its own, so it cannot be blamed on, or hidden by, another test.
/// </summary>
public class ServerFailureTests(SampleHost host)
{
    [Theory]
    [InlineData("#throw", "Deliberate fault: event handler.")]
    [InlineData("#throw-later", "Deliberate fault: after a delay.")]
    public async Task ATest_Fails_IfServerCodeThrewInTheCircuit(string button, string message)
    {
        await using var server = SampleServer.Start(faults: true);
        var probe = await ProbeTest.StartAsync(host, server);
        await probe.OpenAsync("faults");
        await probe.CurrentPage.ClickAsync(button);

        // Set aside what the browser noticed, to show that the server's own log is enough.
        await TakeUntilTheCircuitIsLostAsync(probe.CurrentWatch);

        var thrown = await Should.ThrowAsync<FailException>(probe.EndAsync);

        thrown.Message.ShouldContain(message);
        thrown.Message.ShouldContain("CircuitUnhandledException");
    }

    [Theory]
    [InlineData("throw", "Deliberate fault: disposal.")]
    [InlineData("js", "JSDisconnectedException")]
    public async Task ATest_Fails_IfDisposalThrowsOnceTheTestIsOver(string disposal, string expected)
    {
        await using var server = SampleServer.Start(faults: true);
        var probe = await ProbeTest.StartAsync(host, server);
        await probe.OpenAsync($"faults?disposal={disposal}");
        await Expect(probe.CurrentPage.Locator("#faulty-disposal")).ToBeVisibleAsync();

        // Nothing has gone wrong yet, and nothing will where the browser can see it.
        var thrown = await Should.ThrowAsync<FailException>(probe.EndAsync);

        thrown.Message.ShouldContain(expected);
        thrown.Message.ShouldNotContain(nameof(PageFailureKind.ConnectionLost));
        server.Log.TakeFailures().ShouldBeEmpty("ending the test claims what the server logged");
    }

    [Fact]
    public async Task ATest_Fails_IfAMethodInvokedFromScriptThrew_ThoughTheScriptIgnoredIt()
    {
        await using var server = SampleServer.Start(faults: true);
        var probe = await ProbeTest.StartAsync(host, server);
        await probe.OpenAsync("faults");
        await probe.CurrentPage.EvaluateAsync(
            "() => DotNet.invokeMethodAsync('RichContent.Sample', 'DeliberateFault').catch(() => {})");

        var thrown = await Should.ThrowAsync<FailException>(probe.EndAsync);

        thrown.Message.ShouldContain("Deliberate fault: invoked from script.");
    }

    [Fact]
    public async Task ATest_Passes_WhenTheServerLoggedNoFailure()
    {
        await using var server = SampleServer.Start(faults: true);
        var probe = await ProbeTest.StartAsync(host, server);
        await probe.OpenAsync("faults");

        await probe.EndAsync();
    }

    [Fact]
    public async Task TheRun_FailsAtShutdown_IfAServerFailureWasNeverClaimed()
    {
        // A second host, shut down by hand the way xunit shuts down the shared one.
        var other = new SampleHost();
        await other.InitializeAsync();
        other.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Deliberate").LogError("A failure no test saw.");

        var thrown = await Should.ThrowAsync<FailException>(() => other.DisposeAsync().AsTask());

        thrown.Message.ShouldContain("A failure no test saw.");
    }

    [Fact]
    public async Task TheFaultsPage_IsOffUnlessConfigured()
    {
        await using var context = await host.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(host.Address(), "faults?disposal=throw").AbsoluteUri);

        await Expect(page.GetByText("Not found.")).ToBeVisibleAsync();
        await Expect(page.Locator("button")).ToHaveCountAsync(0);
    }

    private static async Task TakeUntilTheCircuitIsLostAsync(PageWatch watch)
    {
        var failures = new List<PageFailure>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!failures.Any(f => f.Kind == PageFailureKind.ConnectionLost) && DateTime.UtcNow < deadline)
        {
            failures.AddRange(await watch.TakeFailuresAsync());
        }

        failures.ShouldContain(f => f.Kind == PageFailureKind.ConnectionLost);
    }
}
