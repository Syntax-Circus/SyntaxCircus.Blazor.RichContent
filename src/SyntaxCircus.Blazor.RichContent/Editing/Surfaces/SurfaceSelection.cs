namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>The formatting at the cursor in a writing surface, as its script reports it.</summary>
/// <param name="LinkUrl">The address of the link at the cursor, or null outside a link. It may not be a safe one.</param>
/// <param name="Style"><c>p</c>, <c>h2</c>, <c>h3</c> or <c>h4</c>.</param>
/// <param name="List"><c>none</c>, <c>bullets</c> or <c>numbers</c>: the nearest list around the cursor.</param>
public sealed record SurfaceSelection(
    bool Bold, bool Italic, bool Code, string? LinkUrl, string Style, string List, bool Quote, bool InListItem);
