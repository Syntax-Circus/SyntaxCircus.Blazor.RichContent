using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.Rendering;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Tests;

public sealed partial class RenderingTests
{
    [Fact]
    public async Task DocumentLevelError_DoesNotSuppressBlocks()
    {
        var html = await Render<ContentDocumentView>(new()
        {
            ["Document"] = Doc(Rich("a", Para(Text("First"))), Rich("b", Para(Text("Second")))),
            ["Limits"] = new RichContentLimits(Blocks: 1),
        });

        html.ShouldContain("First");
        html.ShouldContain("Second");
    }

    private sealed class CapturingProvider(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(logLevel + ": " + formatter(state, exception));
    }

    [Fact]
    public async Task SkippedBlocks_LogWarningWithIdsAndCodes_NeverContent()
    {
        var lines = new List<string>();
        using var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(new CapturingProvider(lines)))
            .AddSingleton<IErrorBoundaryLogger, TestErrorBoundaryLogger>()
            .AddSingleton<IRichContentMediaResolver>(new Resolver())
            .AddRichContent()
            .BuildServiceProvider();
        var bad = new RichTextNode(RichTextNodeKind.Link, Url: "javascript:alert(1)", Children: [Text("SecretText")]);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ContentDocumentView>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Document"] = Doc(Rich("bad-block", Para(bad))) })));

        var line = lines.ShouldHaveSingleItem();
        line.ShouldStartWith("Warning");
        line.ShouldContain("bad-block");
        line.ShouldContain("unsafe-url");
        line.ShouldNotContain("SecretText");
    }

    [Fact]
    public async Task RichTextView_UsesConfiguredUrlLimit()
    {
        var link = new RichTextNode(RichTextNodeKind.Link, Url: "https://example.com/long-path", Children: [Text("txt")]);

        var html = await Render<RichTextView>(new() { ["Nodes"] = ImmutableArray.Create(Para(link)), ["UrlCharacters"] = 10 });

        html.ShouldContain("txt");
        html.ShouldNotContain("<a");
    }

    [Fact]
    public async Task CtaBlockView_UsesConfiguredUrlLimit()
    {
        var html = await Render<CtaBlockView>(new() { ["Block"] = new CtaBlock("c", "Head", null, "Go", "https://example.com/long-path"), ["UrlCharacters"] = 10 });

        html.ShouldNotContain("<a");
    }

    [Fact]
    public async Task DuplicateBlockIds_RenderWithoutThrowing()
    {
        var html = await RenderDocument(Doc(Rich("same", Para(Text("x"))), Rich("same", Para(Text("y")))));

        html.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Bad Type")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("has_underscore")]
    public void Registry_RejectsInvalidType(string type)
    {
        Should.Throw<ArgumentException>(() => new ContentBlockRegistry().Add(type, "L", typeof(ProjectCard)));
    }

    [Fact]
    public void Registry_RejectsTooLongType()
    {
        Should.Throw<ArgumentException>(() => new ContentBlockRegistry().Add(new string('a', 41), "L", typeof(ProjectCard)));
    }

    [Fact]
    public void Registry_RejectsRendererThatIsNotAComponent()
    {
        Should.Throw<ArgumentException>(() => new ContentBlockRegistry().Add("a", "A", typeof(string)));
    }

    [Fact]
    public void Registry_RejectsEditorNotDerivingExtensionBlockEditorBase()
    {
        Should.Throw<ArgumentException>(() => new ContentBlockRegistry().Add("a", "A", typeof(ProjectCard), typeof(ProjectCard)));
    }
}
