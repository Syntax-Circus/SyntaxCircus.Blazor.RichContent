namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>How <see cref="ContentDocumentEditor"/> presents rich text.</summary>
public enum RichContentEditorMode
{
    /// <summary>One labelled field for every node, for structural work. Needs no JavaScript.</summary>
    Blocks,

    /// <summary>Prose is edited as continuous formatted text. Needs interactive rendering and JavaScript.</summary>
    Write,
}
