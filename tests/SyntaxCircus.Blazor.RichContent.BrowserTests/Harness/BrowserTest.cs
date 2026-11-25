using System.Diagnostics;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// Base of every browser test. Each test gets its own browser context and page. When it ends, it leaves its pages,
/// which disposes their circuits, and fails if a page reported anything that <see cref="PageWatch"/> records or a
/// server logged anything that <see cref="ServerLog"/> keeps.
/// </summary>
public abstract class BrowserTest(SampleHost host) : IAsyncLifetime
{
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(15);

    private IBrowserContext? _context;

    protected SampleHost Host { get; } = host;

    protected IPage Page { get; private set; } = default!;

    protected PageWatch Watch { get; private set; } = default!;

    /// <summary>The servers this test talks to. Their logged failures fail it.</summary>
    protected virtual IReadOnlyList<SampleServer> Servers => [Host.Server, Host.StrictServer];

    public virtual async ValueTask InitializeAsync()
    {
        _context = await Host.Browser.NewContextAsync();
        Watch = await PageWatch.AttachAsync(_context);
        Page = await _context.NewPageAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_context is null)
        {
            return;
        }

        try
        {
            var problems = new List<string>();
            problems.AddRange((await Watch.TakeFailuresAsync()).Select(f => f.ToString()));
            try
            {
                await LeavePagesAsync(_context);
                problems.AddRange((await Watch.TakeFailuresAsync()).Select(f => f.ToString()));
            }
            catch (Exception exception)
            {
                problems.Add("Leaving the pages at the end of the test failed: " + exception.Message);
            }

            problems.AddRange(Servers.SelectMany(server => server.Log.TakeFailures()).Select(f => f.ToString()));
            if (problems.Count > 0)
            {
                Assert.Fail(
                    "The test left failures (a server shared with other tests may report one of theirs):" + Environment.NewLine
                    + string.Join(Environment.NewLine, problems));
            }
        }
        finally
        {
            await _context.CloseAsync();
        }
    }

    /// <summary>Opens <c>/fixture/{name}</c> and waits until the circuit is up and the page is interactive.</summary>
    protected Task OpenFixtureAsync(string name, string mode = "blocks", bool strictCsp = false) =>
        FixturePage.OpenAsync(Page, Host.Address(strictCsp), name, mode);

    // A server disposes a circuit at once when its page is left. It keeps one whose context is simply closed, and
    // does not dispose it even when it shuts down, so disposal code would never run and could never be seen to fail.
    private async Task LeavePagesAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(p => !p.IsClosed))
        {
            // A page left while its circuit is still starting makes the server log errors of its own, or leaves
            // before there is a circuit to dispose. Pages that mark themselves are waited for first.
            var marker = page.Locator("[data-interactive]");
            if (await marker.CountAsync() > 0)
            {
                await Assertions.Expect(marker).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });
            }

            var connections = await Watch.OpenConnectionIdsAsync(page);
            if (connections.Count == 0)
            {
                continue;
            }

            foreach (var connection in connections)
            {
                await WaitUntilAsync(
                    () => Servers.Any(server => server.Log.Served(connection)),
                    "None of the test's servers ran the circuit of an open page.");
            }

            // One round trip first, so the circuit has taken up everything the page sent before it goes.
            if (await page.Locator("#fixture[data-interactive=true] #save").CountAsync() == 1)
            {
                await FixturePage.SaveAsync(page);
            }

            await page.GotoAsync("about:blank");
            foreach (var connection in connections)
            {
                await WaitUntilAsync(
                    () => Servers.Any(server => server.Log.HasDisposedCircuitOf(connection)),
                    "The server did not dispose the circuit of a page that was left.");
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > ServerTimeout)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(20);
        }
    }
}
