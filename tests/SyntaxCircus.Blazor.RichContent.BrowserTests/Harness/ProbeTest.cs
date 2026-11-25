using static Microsoft.Playwright.Assertions;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// A second browser test, driven by hand the way xunit drives one, for showing what makes a test fail. It has a
/// server of its own: a failure caused on purpose must not be blamed on, or taken by, a test running beside it.
/// </summary>
internal sealed class ProbeTest : BrowserTest
{
    private ProbeTest(SampleHost host, SampleServer server)
        : base(host) => Server = server;

    public SampleServer Server { get; }

    public IPage CurrentPage => Page;

    public PageWatch CurrentWatch => Watch;

    protected override IReadOnlyList<SampleServer> Servers => [Server];

    public static async Task<ProbeTest> StartAsync(SampleHost host, SampleServer server)
    {
        var probe = new ProbeTest(host, server);
        await probe.InitializeAsync();
        return probe;
    }

    /// <summary>Opens a page of the probe's server and waits until it is interactive.</summary>
    public async Task OpenAsync(string path)
    {
        await Page.GotoAsync(new Uri(Server.Address, path).AbsoluteUri);
        await Expect(Page.Locator("[data-interactive]")).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });
    }

    /// <summary>Ends the test as xunit does; it throws if the test is to fail.</summary>
    public Task EndAsync() => DisposeAsync().AsTask();
}
