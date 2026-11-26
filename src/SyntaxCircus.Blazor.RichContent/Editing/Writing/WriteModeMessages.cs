namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>What Write mode tells the author about content it could not save, show or move.</summary>
internal static class WriteModeMessages
{
    /// <summary>The error code of content that is on screen but not in the document.</summary>
    public const string UnconvertibleCode = "surface-unconvertible";

    public const string Unconvertible =
        "What is shown here could not be saved, so the document does not have the latest changes to it. "
        + "Shorten or simplify it, or undo.";

    public const string Stalled =
        "The latest content could not be shown here, so it cannot be edited yet. Trying again.";

    public const string ToolsStalled = "The writing tools have not loaded yet, so the text cannot be edited. Trying again.";

    public const string LoadFailed =
        "The writing view could not be loaded, so the block editor is shown. Choose Write to try again.";

    public const string SwitchRefused =
        "The view was not switched: some text here could not be saved and would be lost. "
        + "Shorten or simplify it, or undo, first.";

    public const string RearrangeRefused =
        "This was not done: some text it would redraw could not be saved and would be lost. "
        + "Shorten or simplify it, or undo, first.";

    public const string LinkTooLong =
        "This link's address is too long to edit here, and only its start is shown, so nothing in this box can be "
        + "applied. Edit it in Blocks mode, or use Remove link and then link the text again.";

    public const string InsertNotReady = "Nothing was inserted: the text here was still being saved. Try again.";
}
