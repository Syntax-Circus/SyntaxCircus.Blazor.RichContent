using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

[assembly: AssemblyFixture(typeof(SampleHost))]

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// Shared by the whole test run: the sample app (once as it is and once with its strict Content-Security-Policy,
/// see <see cref="SampleServer"/>) and one browser.
/// </summary>
public sealed class SampleHost : IAsyncLifetime
{
    private const string BrowserVariable = "RICHCONTENT_BROWSER";

    private SampleServer? _server;
    private SampleServer? _strictServer;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary><c>chromium</c> (default), <c>firefox</c> or <c>webkit</c>, from <c>RICHCONTENT_BROWSER</c>.</summary>
    public static string BrowserName { get; } =
        Environment.GetEnvironmentVariable(BrowserVariable) is { Length: > 0 } name ? name.ToLowerInvariant() : BrowserTypeNames.Chromium;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("The browser has not started.");

    public SampleServer Server => _server ?? throw new InvalidOperationException("The sample has not started.");

    public SampleServer StrictServer => _strictServer ?? throw new InvalidOperationException("The sample has not started.");

    public IServiceProvider Services => Server.Services;

    public Uri Address(bool strictCsp = false) => (strictCsp ? StrictServer : Server).Address;

    public async ValueTask InitializeAsync()
    {
        _server = SampleServer.Start();
        _strictServer = SampleServer.Start(strictCsp: true);

        _playwright = await Playwright.CreateAsync();
        var type = BrowserName switch
        {
            BrowserTypeNames.Chromium => _playwright.Chromium,
            BrowserTypeNames.Firefox => _playwright.Firefox,
            BrowserTypeNames.Webkit => _playwright.Webkit,
            _ => throw new InvalidOperationException($"{BrowserVariable} must be chromium, firefox or webkit, not '{BrowserName}'."),
        };
        _browser = await type.LaunchAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        var unclaimed = new List<ServerFailure>();
        foreach (var server in new[] { _server, _strictServer })
        {
            if (server is not null)
            {
                await server.DisposeAsync();

                // Taken after the server has stopped, so that stopping it is covered too.
                unclaimed.AddRange(server.Log.TakeFailures());
            }
        }

        // The last chance to notice anything logged after the test that caused it had ended. Failing here fails
        // the run, although no single test.
        if (unclaimed.Count > 0)
        {
            Assert.Fail("The shared sample servers logged failures that no test claimed:" + Environment.NewLine + string.Join(Environment.NewLine, unclaimed));
        }
    }

    private static class BrowserTypeNames
    {
        public const string Chromium = "chromium";
        public const string Firefox = "firefox";
        public const string Webkit = "webkit";
    }
}
