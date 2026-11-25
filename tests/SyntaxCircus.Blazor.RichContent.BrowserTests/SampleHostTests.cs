using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;
using static SyntaxCircus.Blazor.RichContent.BrowserTests.Harness.FixturePage;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>Guards the conditions the other browser tests assume about the host.</summary>
public class SampleHostTests(SampleHost host) : BrowserTest(host)
{
    private const string StrictPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:";

    [Fact]
    public void IncomingMessages_AreStillLimitedTo32KB() =>
        Host.Services.GetRequiredService<IOptions<HubOptions>>().Value.MaximumReceiveMessageSize.ShouldBe(32 * 1024);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictPolicy_IsTheOnlyPolicyHeader_AndOnlyFromTheStrictHost(bool strictCsp)
    {
        Host.Address(strictCsp).IsLoopback.ShouldBeTrue();

        var response = await Page.GotoAsync(new Uri(Host.Address(strictCsp), "fixture/empty").AbsoluteUri);

        // What the browser received. Playwright's WebKit on Windows keeps only the last of two headers with one
        // name, so a second policy header would silently switch the strict one off there.
        var policy = await response!.HeaderValueAsync("content-security-policy");
        policy.ShouldBe(strictCsp ? StrictPolicy : "frame-ancestors 'self'");
    }

    [Theory]
    [InlineData("everything")]
    [InlineData("post")]
    public async Task StrictPolicy_FixtureLoadsAndSaves_WithoutViolations(string name)
    {
        await OpenFixtureAsync(name, strictCsp: true);

        (await SaveAsync(Page)).ShouldNotBeEmpty();
    }
}
