using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>
/// One thing Insert offers in Write mode: either a rich-text node, which goes among the nodes where the cursor is,
/// or a block, which splits the rich text block there. Exactly one of <paramref name="NodeKind"/> and
/// <paramref name="CreateBlock"/> is set.
/// </summary>
public sealed record InsertChoice(string Label, bool NeedsMedia, RichTextNodeKind? NodeKind, Func<ContentBlock>? CreateBlock)
{
    /// <summary>The code block, image and table that fit within <paramref name="maxDepth"/> levels.</summary>
    public static IReadOnlyList<InsertChoice> ForNodes(int maxDepth) =>
    [
        .. new InsertChoice[]
        {
            new("Code block", false, RichTextNodeKind.CodeBlock, null),
            new("Image", true, RichTextNodeKind.Image, null),
            new("Table", false, RichTextNodeKind.Table, null),
        }.Where(choice => RichTextEditorOperations.ScaffoldDepth(choice.NodeKind!.Value) <= maxDepth),
    ];
}
