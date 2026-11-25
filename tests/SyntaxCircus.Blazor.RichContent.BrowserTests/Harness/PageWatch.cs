using System.Collections.Concurrent;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

public enum PageFailureKind { ConsoleError, PageError, CspViolation, ConnectionLost }

public sealed record PageFailure(PageFailureKind Kind, string Detail)
{
    public override string ToString() => $"{Kind}: {Detail}";
}

/// <summary>
/// Records everything that must fail a browser test: console errors, uncaught page errors, Content-Security-Policy
/// violations and a lost Blazor connection (which is how a lost circuit shows in the browser). Attach it to a
/// context before opening any page. The one exception is a deliberate outage; see <see cref="DropConnectionAsync"/>.
/// </summary>
public sealed class PageWatch
{
    private const string ReportBinding = "__pageWatchReport";
    private const string NegotiateRoute = "**/_blazor/negotiate**";

    // Blazor's built-in reconnect dialog; the sample does not replace it.
    private const string ReconnectDialogShows = "document.getElementById('components-reconnect-modal')?.checkVisibility() === true";

    // Runs in every page before any of its own scripts. Playwright injects it, so a page's CSP does not apply.
    private const string Script = """
        (() => {
            const report = (kind, detail) => window.__pageWatchReport(kind, detail);

            document.addEventListener('securitypolicyviolation', event =>
                report('CspViolation', `${event.violatedDirective} blocked ${event.blockedURI || 'inline content'}`), true);

            // Leaving the page closes its socket, which is not a lost connection.
            let leaving = false;
            window.addEventListener('beforeunload', () => leaving = true);
            window.addEventListener('pagehide', () => leaving = true);

            const NativeWebSocket = window.WebSocket;
            const open = new Set();
            window.WebSocket = class extends NativeWebSocket {
                constructor(...args) {
                    super(...args);
                    if (!new URL(this.url).pathname.endsWith('/_blazor')) {
                        return;
                    }

                    this.addEventListener('open', () => open.add(this));
                    this.addEventListener('close', event => {
                        open.delete(this);
                        if (!leaving) {
                            report('ConnectionLost', `the Blazor socket closed (code ${event.code} ${event.reason})`);
                        }
                    });
                }
            };

            window.__pageWatch = {
                openSockets: () => open.size,
                closeSockets: () => open.forEach(socket => socket.close()),
            };
        })();
        """;

    private readonly IBrowserContext _context;
    private readonly ConcurrentQueue<PageFailure> _failures = new();
    private volatile bool _outage;

    private PageWatch(IBrowserContext context) => _context = context;

    public static async Task<PageWatch> AttachAsync(IBrowserContext context)
    {
        var watch = new PageWatch(context);
        context.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                watch.Add(new(PageFailureKind.ConsoleError, $"{message.Text} ({message.Location})"));
            }
        };
        context.WebError += (_, error) => watch.Add(new(PageFailureKind.PageError, error.Error));
        await context.ExposeFunctionAsync<string, string, bool>(ReportBinding, (kind, detail) =>
        {
            watch.Add(new(Enum.Parse<PageFailureKind>(kind), detail));
            return true;
        });
        await context.AddInitScriptAsync(Script);
        return watch;
    }

    /// <summary>The number of Blazor sockets the watch can see. Zero means a lost connection would go unnoticed.</summary>
    public static Task<int> OpenSocketsAsync(IPage page) => page.EvaluateAsync<int>("() => window.__pageWatch.openSockets()");

    /// <summary>Returns the failures recorded so far and forgets them. For tests of the watch itself.</summary>
    public async Task<IReadOnlyList<PageFailure>> TakeFailuresAsync()
    {
        await FlushAsync();
        var taken = new List<PageFailure>();
        while (_failures.TryDequeue(out var failure))
        {
            taken.Add(failure);
        }

        return taken;
    }

    public async Task AssertCleanAsync()
    {
        await FlushAsync();
        if (!_failures.IsEmpty)
        {
            Assert.Fail("The page reported failures:" + Environment.NewLine + string.Join(Environment.NewLine, _failures));
        }
    }

    /// <summary>
    /// Cuts the Blazor connection and keeps it from coming back until <see cref="Outage.RestoreAsync"/>. Until
    /// then console errors and the lost connection are expected and not recorded. Uncaught page errors and CSP
    /// violations still fail the test. A test that never restores keeps the tolerance for itself only.
    /// </summary>
    public async Task<Outage> DropConnectionAsync(IPage page)
    {
        // Anything that happened before the outage must still count.
        await FlushAsync();
        var instance = await InstanceAsync(page);
        _outage = true;
        await _context.RouteAsync(NegotiateRoute, route => route.AbortAsync());
        await page.EvaluateAsync("() => window.__pageWatch.closeSockets()");
        // Wait until Blazor has noticed too. It hides this dialog again only once it has rejoined its circuit.
        await page.WaitForFunctionAsync($"() => window.__pageWatch.openSockets() === 0 && ({ReconnectDialogShows})");
        return new Outage(this, page, instance);
    }

    private static async Task<string?> InstanceAsync(IPage page)
    {
        var fixture = page.Locator("#fixture");
        return await fixture.CountAsync() == 0 ? null : await fixture.GetAttributeAsync("data-instance");
    }

    private void Add(PageFailure failure)
    {
        if (_outage && failure.Kind is PageFailureKind.ConsoleError or PageFailureKind.ConnectionLost)
        {
            return;
        }

        _failures.Enqueue(failure);
    }

    // Events arrive asynchronously. A short timer in each page, answered over the same channel, lets queued
    // violation events and console messages arrive first.
    private async Task FlushAsync()
    {
        foreach (var page in _context.Pages)
        {
            if (!page.IsClosed)
            {
                await page.EvaluateAsync("() => new Promise(resolve => setTimeout(resolve, 100))");
            }
        }
    }

    public sealed class Outage
    {
        private readonly PageWatch _watch;
        private readonly IPage _page;
        private readonly string? _instance;

        internal Outage(PageWatch watch, IPage page, string? instance) => (_watch, _page, _instance) = (watch, page, instance);

        /// <summary>
        /// Lets the page reconnect and waits until it has, to the same circuit: a reload or a new circuit fails.
        /// Failures count again afterwards.
        /// </summary>
        public async Task RestoreAsync()
        {
            await _watch._context.UnrouteAsync(NegotiateRoute);
            await _page.WaitForFunctionAsync(
                $"() => window.__pageWatch.openSockets() > 0 && !({ReconnectDialogShows})",
                null,
                new() { Timeout = 60_000 });
            await _watch.FlushAsync();
            (await InstanceAsync(_page)).ShouldBe(_instance, "the circuit was lost instead of reconnected");
            _watch._outage = false;
        }
    }
}
