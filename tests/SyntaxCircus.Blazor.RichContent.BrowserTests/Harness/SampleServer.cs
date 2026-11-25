using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using RichContent.Sample.Components;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// The sample app on Kestrel on a free loopback port, with what it logged as a failure in <see cref="Log"/>. Hub
/// options are left at their defaults on purpose, so incoming messages are limited to 32 KB as they are for a host
/// that changes nothing.
/// </summary>
public sealed class SampleServer : WebApplicationFactory<App>
{
    private readonly bool _strictCsp;
    private readonly bool _faults;

    private SampleServer(bool strictCsp, bool faults) => (_strictCsp, _faults) = (strictCsp, faults);

    public ServerLog Log { get; } = new();

    public Uri Address => ClientOptions.BaseAddress;

    /// <param name="strictCsp">Send the sample's strict Content-Security-Policy.</param>
    /// <param name="faults">Serve <c>/faults</c>, which fails on the server on request.</param>
    public static SampleServer Start(bool strictCsp = false, bool faults = false)
    {
        var server = new SampleServer(strictCsp, faults);

        // A real server: the browser cannot reach the in-memory test server.
        server.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        server.StartServer();
        return server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Sample:StrictCsp", _strictCsp ? "true" : "false");
        builder.UseSetting("Sample:Faults", _faults ? "true" : "false");
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(Log);
            logging.AddFilter<ServerLog>(ServerLog.Wants);
        });
    }
}
