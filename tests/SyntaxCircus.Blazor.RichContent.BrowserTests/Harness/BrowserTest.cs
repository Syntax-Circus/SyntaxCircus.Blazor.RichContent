namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// Base of every browser test. Each test gets its own browser context and page, and fails when it ends if the
/// page reported anything that <see cref="PageWatch"/> records.
/// </summary>
public abstract class BrowserTest(SampleHost host) : IAsyncLifetime
{
    private IBrowserContext? _context;

    protected SampleHost Host { get; } = host;

    protected IPage Page { get; private set; } = default!;

    protected PageWatch Watch { get; private set; } = default!;

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
            await Watch.AssertCleanAsync();
        }
        finally
        {
            await _context.CloseAsync();
        }
    }

    /// <summary>Opens <c>/fixture/{name}</c> and waits until the circuit is up and the page is interactive.</summary>
    protected Task OpenFixtureAsync(string name, string mode = "blocks", bool strictCsp = false) =>
        FixturePage.OpenAsync(Page, Host.Address(strictCsp), name, mode);
}
