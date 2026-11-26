namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>
/// The collection of the test classes that cut, copy or paste for real. Some engines use the machine's one clipboard,
/// so these must not run beside each other: put any other test that touches the clipboard in one of them.
/// </summary>
public static class ClipboardTests
{
    public const string Name = "clipboard";
}
