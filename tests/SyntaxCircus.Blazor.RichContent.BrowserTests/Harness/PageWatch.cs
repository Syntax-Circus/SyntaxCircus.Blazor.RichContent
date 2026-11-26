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

            // Leaving the page closes its socket, which is not a lost connection. Only pagehide shows that the page
            // really is going: beforeunload also fires when it then stays (a download, a mailto: link, a response
            // with no content, a cancelled navigation). A page can be shown again, from the back/forward cache.
            //
            // Chromium and WebKit close the socket of a departing page without telling it. Firefox tells it, after
            // beforeunload and before pagehide, and under load more than a second after beforeunload. So no time
            // limit tells such a close from a real loss. Every loss after beforeunload is held instead, and recorded
            // unless pagehide follows:
            // - if the page is still here when the watch next looks, it is recorded;
            // - if beforeunload fires again, it is recorded at once: it came before the navigation that is starting,
            //   so was no part of it. That is how a reload after a lost circuit cannot take the loss away.
            // A loss with no beforeunload before it is recorded at once.
            let leaving = false;
            let unloadAsked = false;
            let held = [];
            const reportHeld = () => Promise.all(held.splice(0).map(detail => report('ConnectionLost', detail)));
            window.addEventListener('beforeunload', () => {
                reportHeld();
                unloadAsked = true;
            });
            window.addEventListener('pagehide', () => {
                leaving = true;
                held = [];
            });
            window.addEventListener('pageshow', () => {
                leaving = false;
                unloadAsked = false;
            });
            const lost = detail => {
                if (unloadAsked) {
                    held.push(detail);
                } else {
                    report('ConnectionLost', detail);
                }
            };

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
                            lost(`the Blazor socket closed (code ${event.code} ${event.reason})`);
                        }
                    });
                }
            };

            window.__pageWatch = {
                openSockets: () => open.size,
                openTokens: () => [...open].map(socket => new URL(socket.url).searchParams.get('id')),
                // Resolves once they have closed and been dealt with. Blazor reconnects at once, so waiting for
                // openSockets to reach zero afterwards can miss the moment.
                closeSockets: () => Promise.all([...open].map(socket => new Promise(resolve => {
                    socket.addEventListener('close', () => resolve(), { once: true });
                    socket.close();
                }))),
                reportHeld,
            };
        })();
        """;

    private readonly IBrowserContext _context;
    private readonly ConcurrentQueue<PageFailure> _failures = new();
    private readonly ConcurrentQueue<Task> _negotiations = new();
    private readonly ConcurrentDictionary<string, string> _connectionIdsByToken = new(StringComparer.Ordinal);
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
        context.Response += (_, response) =>
        {
            if (new Uri(response.Url).AbsolutePath.EndsWith("/_blazor/negotiate", StringComparison.Ordinal))
            {
                watch._negotiations.Enqueue(watch.ReadNegotiationAsync(response));
            }
        };
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

    /// <summary>
    /// The SignalR connection ids behind the page's open Blazor sockets, which is how a server's log names them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> OpenConnectionIdsAsync(IPage page)
    {
        var tokens = await page.EvaluateAsync<string[]>("() => window.__pageWatch?.openTokens() ?? []");
        await Task.WhenAll(_negotiations);
        return
        [
            .. tokens.Select(token => _connectionIdsByToken.TryGetValue(token, out var id)
                ? id
                : throw new ShouldAssertException("The watch did not see the negotiation of an open Blazor connection.")),
        ];
    }

    // The socket's address carries the connection token; the server's log names the connection id.
    private async Task ReadNegotiationAsync(IResponse response)
    {
        try
        {
            if (await response.JsonAsync() is { } body
                && body.TryGetProperty("connectionToken", out var token)
                && body.TryGetProperty("connectionId", out var id))
            {
                _connectionIdsByToken[token.GetString()!] = id.GetString()!;
            }
        }
        catch (PlaywrightException)
        {
            // The page left before the body was read, so there will be no socket to look up.
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
    // violation events and console messages arrive first. A page that is still here then reports what it held.
    private async Task FlushAsync()
    {
        foreach (var page in _context.Pages)
        {
            if (!page.IsClosed)
            {
                await page.EvaluateAsync(
                    "async () => { await new Promise(resolve => setTimeout(resolve, 100)); await window.__pageWatch?.reportHeld(); }");
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
