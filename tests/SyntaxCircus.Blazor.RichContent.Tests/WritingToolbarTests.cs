using System.Collections.Immutable;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Blazor.RichContent.Editing;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;
using Kind = SyntaxCircus.RichContent.RichTextNodeKind;

namespace SyntaxCircus.Blazor.RichContent.Tests;

/// <summary>The writing toolbar on the .NET side, with the script stood in for.</summary>
public sealed class WritingToolbarTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly BunitJSModuleInterop _module;

    public WritingToolbarTests()
    {
        _context.Services.AddRichContent();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _context.JSInterop.SetupModule(RichTextSurface.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<int?>("sync", _ => true).SetResult(1);
    }

    public void Dispose() => _context.Dispose();

    private static ContentDocument OneBlock() => new(ContentDocument.CurrentSchemaVersion,
        [new RichTextBlock("one", [new RichTextNode(Kind.Paragraph, Children: [new RichTextNode(Kind.Text, Text: "text")])])]);

    private static SurfaceSelection At(
        string? link = null, string style = "p", string list = "none", bool quote = false, bool inListItem = false, bool bold = false) =>
        new(bold, false, false, link, style, list, quote, inListItem);

    private static bool Enabled(IRenderedComponent<ContentDocumentEditor> editor, string selector) =>
        !editor.Find($"[role=toolbar] {selector}").HasAttribute("disabled");

    private IRenderedComponent<ContentDocumentEditor> Render(
        RichContentLimits? limits = null, Action<ContentDocument>? changed = null) =>
        _context.Render<ContentDocumentEditor>(parameters =>
        {
            parameters.Add(editor => editor.Document, OneBlock()).Add(editor => editor.Mode, RichContentEditorMode.Write)
                .Add(editor => editor.Limits, limits);
            if (changed is not null)
            {
                parameters.Add(editor => editor.DocumentChanged, changed);
            }
        });

    private static Task Report(IRenderedComponent<ContentDocumentEditor> editor, SurfaceSelection selection)
    {
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        return editor.InvokeAsync(() => surface.SelectionChanged(selection));
    }

    [Fact]
    public void BlocksMode_HasNoToolbar() =>
        _context.Render<ContentDocumentEditor>(parameters => parameters.Add(editor => editor.Document, OneBlock()))
            .FindAll("[role=toolbar]").ShouldBeEmpty();

    [Fact]
    public void LeavingWriteMode_TellsTheScriptToLetGoOfThatToolbar_AndANewOneIsNamedAnew()
    {
        var editor = Render();
        var first = _module.Invocations["attachToolbar"].ShouldHaveSingleItem().Arguments[2];

        editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Blocks));

        _module.Invocations["detachToolbar"].ShouldHaveSingleItem().Arguments[0].ShouldBe(first);
        editor.Render(parameters => parameters.Add(e => e.Mode, RichContentEditorMode.Write));
        _module.Invocations["attachToolbar"].Count.ShouldBe(2);
        _module.Invocations["attachToolbar"][1].Arguments[2].ShouldNotBe(first);
    }

    [Fact]
    public async Task TheToolbar_FollowsTheReportedState_AndRaisesNoChange()
    {
        var changes = 0;
        var editor = Render(changed: _ => changes++);
        editor.FindAll("[role=toolbar] button, [role=toolbar] select").ShouldAllBe(control => control.HasAttribute("disabled"));

        await Report(editor, At(bold: true, style: "h3", list: "numbers", link: "/x"));

        editor.Find("[data-rt-command=bold]").GetAttribute("aria-pressed").ShouldBe("true");
        editor.Find("[data-rt-command=italic]").GetAttribute("aria-pressed").ShouldBe("false");
        editor.Find("[data-rt-command=numbers]").GetAttribute("aria-pressed").ShouldBe("true");
        editor.Find("[data-rt-command=bullets]").GetAttribute("aria-pressed").ShouldBe("false");
        editor.Find("[role=toolbar] select").GetAttribute("value").ShouldBe("h3");
        Enabled(editor, "select").ShouldBeTrue();
        changes.ShouldBe(0);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, true)]
    public async Task WhereTheModelForbidsThem_ButtonsAreDisabled(bool inListItem, bool quote, bool style, bool quoteButton)
    {
        var editor = Render();

        await Report(editor, At(inListItem: inListItem, quote: quote));

        Enabled(editor, "select").ShouldBe(style);
        Enabled(editor, "[data-rt-command=quote]").ShouldBe(quoteButton);
        Enabled(editor, "[data-rt-command=bullets]").ShouldBeTrue();
    }

    [Fact]
    public async Task ADisabledEditor_DisablesTheWholeToolbar()
    {
        var editor = Render();
        await Report(editor, At());

        editor.Render(parameters => parameters.Add(e => e.Disabled, true));

        editor.FindAll("[role=toolbar] button, [role=toolbar] select").ShouldAllBe(control => control.HasAttribute("disabled"));
    }

    [Fact]
    public async Task AnInlineSurface_HasNoBlockCommands()
    {
        var session = new WritingSession();
        var view = _context.Render<CascadingValue<WritingSession>>(parameters => parameters
            .Add(cascade => cascade.Value, session)
            .AddChildContent<WritingToolbar>()
            .AddChildContent<RichTextSurface>(surface => surface.Add(s => s.Profile, RichTextSurfaceProfile.Inline)));

        await view.InvokeAsync(() => view.FindComponent<RichTextSurface>().Instance.SelectionChanged(At()));

        foreach (var control in new[] { "select", "[data-rt-command=bullets]", "[data-rt-command=numbers]", "[data-rt-command=quote]" })
        {
            view.Find($"[role=toolbar] {control}").HasAttribute("disabled").ShouldBeTrue(control);
        }

        view.Find("[data-rt-command=bold]").HasAttribute("disabled").ShouldBeFalse();
        view.Find("[data-rt-command=code]").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public async Task ARemovedSurface_IsForgotten()
    {
        var editor = Render();
        await Report(editor, At());

        editor.Render(parameters => parameters.Add(e => e.Document, ContentDocument.Empty("other") with { Blocks = [] }));

        editor.Find("[data-rt-command=bold]").HasAttribute("disabled").ShouldBeTrue();
    }

    private (string Name, string Argument)[] Commands() =>
        [.. _module.Invocations["command"].Select(call => ((string)call.Arguments[1]!, (string)call.Arguments[2]!))];

    [Theory]
    [InlineData("javascript:alert(1)", 2048, false)]
    [InlineData("http://example.com/", 2048, false)]
    [InlineData("https://example.com/long/address", 20, false)]
    [InlineData("  https://example.com/  ", 2048, true)]
    [InlineData("mailto:someone@example.com", 2048, true)]
    public async Task OnlyASafeAddress_WithinTheLimits_IsSentToTheScript(string address, int limit, bool accepted)
    {
        var editor = Render(RichContentLimits.Default with { UrlCharacters = limit });
        await Report(editor, At(link: "/old"));
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();
        var input = editor.Find(".rt-link-box input");
        input.GetAttribute("value").ShouldBe("/old");

        input.Input(address);
        await editor.Find(".rt-link-box input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        if (accepted)
        {
            Commands().ShouldBe([("link", address.Trim())]);
            editor.FindAll(".rt-link-box").ShouldBeEmpty();
        }
        else
        {
            Commands().ShouldBeEmpty();
            editor.Find(".rt-link-box [role=alert]").TextContent.ShouldContain("https:");
        }
    }

    [Theory]
    [InlineData("  /page  ", 2048, "/page")]
    [InlineData("", 2048, "")]
    [InlineData("javascript:alert(1)", 2048, null)]
    [InlineData("https://example.com/long/address", 20, null)]
    public async Task TheApplyButton_CarriesTheAddress_OnlyOnceItIsAccepted(string typed, int limit, string? accepted)
    {
        var editor = Render(RichContentLimits.Default with { UrlCharacters = limit });
        await Report(editor, At(link: "/old"));
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();

        editor.Find(".rt-link-box input").Input(typed);

        var apply = editor.Find("[data-rt-link-apply]");
        apply.GetAttribute("data-rt-address").ShouldBe(accepted);
        apply.GetAttribute("data-rt-checked").ShouldBe(accepted is null ? null : typed);
    }

    // The script reports only the start of a very long address. Whatever the box holds then may be that start, or the
    // start with changes, and the host's limit may well accept it.
    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com/the-start-changed")]
    [InlineData("/an-address-typed-afresh")]
    public async Task OnALinkTooLongForTheBox_NoAddressIsApplied(string? typed)
    {
        var editor = Render(RichContentLimits.Default with { UrlCharacters = 8000 });
        await Report(editor, At(link: "https://example.com/" + new string('a', 3980)) with { LinkUrlTruncated = true });
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();
        editor.Find(".rt-link-box [role=alert]").TextContent.ShouldContain("Blocks mode");
        if (typed is not null)
        {
            editor.Find(".rt-link-box input").Input(typed);
        }

        // Nothing for the script to apply by itself, in the click or the key press.
        editor.Find("[data-rt-link-apply]").GetAttribute("data-rt-checked").ShouldBeNull();
        editor.Find("[data-rt-link-apply]").GetAttribute("data-rt-address").ShouldBeNull();
        await editor.Find(".rt-link-box input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await editor.Find("[data-rt-link-apply]").ClickAsync(new MouseEventArgs());

        Commands().ShouldBeEmpty();
        editor.Find(".rt-link-box [role=alert]").TextContent.ShouldContain("Blocks mode");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnALinkTooLongForTheBox_ItCanStillBeRemoved(bool withTheButton)
    {
        var editor = Render(RichContentLimits.Default with { UrlCharacters = 8000 });
        await Report(editor, At(link: "/start") with { LinkUrlTruncated = true });
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();

        if (withTheButton)
        {
            await editor.Find("[data-rt-link-remove]").ClickAsync(new MouseEventArgs());
        }
        else
        {
            editor.Find(".rt-link-box input").Input("");
            editor.Find("[data-rt-link-apply]").GetAttribute("data-rt-address").ShouldBe("");
            await editor.Find(".rt-link-box input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        }

        Commands().ShouldBe([("link", "")]);
    }

    // What the box holds came from where it was opened, whatever the cursor has reached since.
    [Fact]
    public async Task WhetherTheBoxHoldsOnlyAStart_IsSettledWhenItOpens()
    {
        var editor = Render(RichContentLimits.Default with { UrlCharacters = 8000 });
        await Report(editor, At(link: "/start") with { LinkUrlTruncated = true });
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();

        await Report(editor, At(link: "/short"));
        await editor.Find(".rt-link-box input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Commands().ShouldBeEmpty();

        // Opened again where the whole address is known, it works as ever.
        editor.FindAll("[role=toolbar] button").Single(button => button.TextContent == "Link").Click();
        editor.FindAll(".rt-link-box [role=alert]").ShouldBeEmpty();
        await editor.Find(".rt-link-box input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Commands().ShouldBe([("link", "/short")]);
    }

    [Fact]
    public async Task ALinkTheScriptApplied_ClosesTheBox_AndIsNotRunAgain()
    {
        var editor = Render();
        await Report(editor, At());
        var surface = editor.FindComponent<RichTextSurface>().Instance;
        await editor.InvokeAsync(surface.LinkRequested);

        await editor.InvokeAsync(surface.LinkApplied);

        editor.FindAll(".rt-link-box").ShouldBeEmpty();
        Commands().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheKeyboardRequest_OpensTheBox_ForTheSurfaceWithTheCursorOnly()
    {
        var editor = Render();
        var surface = editor.FindComponent<RichTextSurface>().Instance;

        await editor.InvokeAsync(surface.LinkRequested);
        editor.FindAll(".rt-link-box").ShouldBeEmpty();

        await Report(editor, At());
        await editor.InvokeAsync(surface.LinkRequested);

        editor.FindAll(".rt-link-box").Count.ShouldBe(1);
    }
}
