using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using RichContent.Sample.Components;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

[assembly: AssemblyFixture(typeof(SampleHost))]

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// Shared by the whole test run: the sample app on Kestrel (once as it is and once with its strict
/// Content-Security-Policy) and one browser. Hub options are left at their defaults on purpose, so incoming
/// messages are limited to 32 KB as they are for a host that changes nothing.
/// </summary>
public sealed class SampleHost : IAsyncLifetime
{
    private const string BrowserVariable = "RICHCONTENT_BROWSER";

    private readonly SampleServer _server = new(strictCsp: false);
    private readonly SampleServer _strictServer = new(strictCsp: true);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary><c>chromium</c> (default), <c>firefox</c> or <c>webkit</c>, from <c>RICHCONTENT_BROWSER</c>.</summary>
    public static string BrowserName { get; } =
        Environment.GetEnvironmentVariable(BrowserVariable) is { Length: > 0 } name ? name.ToLowerInvariant() : BrowserTypeNames.Chromium;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("The browser has not started.");

    public IServiceProvider Services => _server.Services;

    public Uri Address(bool strictCsp = false) => (strictCsp ? _strictServer : _server).Address;

    public async ValueTask InitializeAsync()
    {
        _server.Start();
        _strictServer.Start();

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
        await _server.DisposeAsync();
        await _strictServer.DisposeAsync();
    }

    private static class BrowserTypeNames
    {
        public const string Chromium = "chromium";
        public const string Firefox = "firefox";
        public const string Webkit = "webkit";
    }

    private sealed class SampleServer(bool strictCsp) : WebApplicationFactory<App>
    {
        public Uri Address => ClientOptions.BaseAddress;

        public void Start()
        {
            // A real server on a free loopback port: the browser cannot reach the in-memory test server.
            UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            StartServer();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Sample:StrictCsp", strictCsp ? "true" : "false");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        }
    }
}
