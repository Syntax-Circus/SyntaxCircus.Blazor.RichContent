namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>What a writing surface holds.</summary>
public enum RichTextSurfaceProfile
{
    /// <summary>Top-level paragraphs, headings, lists and quotes.</summary>
    Prose,

    /// <summary>Inline content only, as in a table cell.</summary>
    Inline,
}
