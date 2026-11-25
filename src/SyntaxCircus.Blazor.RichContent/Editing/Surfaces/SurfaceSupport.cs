using System.Collections.Immutable;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// Decides whether Write mode can edit a node without losing anything. Shape only: content the author can fix
/// in place (an unsafe or empty link address, text over a limit, an emptied paragraph) does not count against a node.
/// </summary>
internal static class SurfaceSupport
{
    // A table's cell content sits below the table, the row and the cell.
    private const int CellContentDepth = 4;

    /// <summary>True for a top-level paragraph, heading, list or quote that a writing surface can reproduce exactly.</summary>
    public static bool CanEdit(RichTextNode node) =>
        node is { Kind: RichTextNodeKind.Paragraph or RichTextNodeKind.Heading or RichTextNodeKind.List or RichTextNodeKind.Quote }
        && HasSupportedShape(node);

    /// <summary>True when table cell content can be reproduced exactly; empty content can.</summary>
    public static bool CanEditInline(ImmutableArray<RichTextNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            return true;
        }

        var allowed = RichTextEditorOperations.AllowedChildren(RichTextNodeKind.TableCell);
        var pending = new Stack<Pending>();
        foreach (var node in nodes)
        {
            if (node is null || !allowed.Contains(node.Kind))
            {
                return false;
            }

            pending.Push(new(node, CellContentDepth, WithinLink: false));
        }

        return AllSupported(pending);
    }

    /// <summary>
    /// True when the node and everything inside it has a supported shape. Where the node itself may be placed
    /// is the caller's concern.
    /// </summary>
    public static bool HasSupportedShape(RichTextNode node)
    {
        var pending = new Stack<Pending>();
        pending.Push(new(node, Depth: 1, WithinLink: false));
        return AllSupported(pending);
    }

    private readonly record struct Pending(RichTextNode Node, int Depth, bool WithinLink);

    // Iterative, and gives up at the depth limit, so a hostile tree cannot exhaust the stack here or in a later renderer.
    private static bool AllSupported(Stack<Pending> pending)
    {
        while (pending.TryPop(out var current))
        {
            var (node, depth, withinLink) = current;
            if (depth > RichContentLimits.Default.Depth || node.Extra is { Count: > 0 } || !HasSupportedFields(node))
            {
                return false;
            }

            if (node.Children.IsDefaultOrEmpty)
            {
                // A surface cannot reliably show or enter these, so rebuilding around one would drop it,
                // along with a link's address or a list's ordering. Other empty nodes can be typed into or lose nothing.
                if (node.Kind is RichTextNodeKind.Link or RichTextNodeKind.List or RichTextNodeKind.Quote)
                {
                    return false;
                }

                continue;
            }

            // Leaf and unrecognised kinds allow no children at all.
            var allowed = RichTextEditorOperations.AllowedChildren(node.Kind);
            var childWithinLink = withinLink || node.Kind == RichTextNodeKind.Link;
            foreach (var child in node.Children)
            {
                if (child is null || !allowed.Contains(child.Kind) || (childWithinLink && child.Kind == RichTextNodeKind.Link))
                {
                    return false;
                }

                pending.Push(new(child, depth + 1, childWithinLink));
            }

            if (node.Kind == RichTextNodeKind.ListItem && !IsSupportedListItem(node.Children))
            {
                return false;
            }
        }

        return true;
    }

    // Text, link address and heading level are required by their kinds; every other field is optional on its own
    // kind. No kind may carry another kind's field.
    private static bool HasSupportedFields(RichTextNode node)
    {
        var kind = node.Kind;
        var hasText = kind is RichTextNodeKind.Text or RichTextNodeKind.InlineCode or RichTextNodeKind.CodeBlock;
        var isImage = kind == RichTextNodeKind.Image;

        return (node.Text is not null) == hasText
            && (node.Url is not null) == (kind == RichTextNodeKind.Link)
            && (kind == RichTextNodeKind.Heading
                ? node.HeadingLevel is >= RichContentLimits.MinimumHeadingLevel and <= RichContentLimits.MaximumHeadingLevel
                : node.HeadingLevel is null)
            && (node.Ordered is null || kind == RichTextNodeKind.List)
            && (node.Language is null || kind == RichTextNodeKind.CodeBlock)
            && (isImage || (node.Media is null && node.Alt is null && node.Caption is null))
            && (node.Header is null || kind == RichTextNodeKind.TableCell);
    }

    // The two forms a surface can write back: inline content or paragraphs (never both), then any nested lists.
    private static bool IsSupportedListItem(ImmutableArray<RichTextNode> children)
    {
        var paragraphs = children[0].Kind == RichTextNodeKind.Paragraph;
        var index = 0;
        while (index < children.Length
            && children[index].Kind != RichTextNodeKind.List
            && (children[index].Kind == RichTextNodeKind.Paragraph) == paragraphs)
        {
            index++;
        }

        while (index < children.Length && children[index].Kind == RichTextNodeKind.List)
        {
            index++;
        }

        return index == children.Length;
    }
}
