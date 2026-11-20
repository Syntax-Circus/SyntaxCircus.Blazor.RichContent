using System.Collections.Immutable;
using System.Text;

namespace SyntaxCircus.RichContent;

/// <summary>
/// Plain-text projections of a <see cref="ContentDocument"/> for listings, reading-time badges and SEO.
/// Unknown and extension blocks contribute no text. Assumes documents were validated (bounded depth).
/// </summary>
public static class RichContentText
{
    private const string BlockSeparator = "\n\n";

    /// <summary>Prose of all blocks, blank line between blocks. Excludes code blocks and alt text.</summary>
    public static string PlainText(ContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var parts = new List<string>();
        foreach (var block in Blocks(document))
        {
            var text = BlockText(block);
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        return string.Join(BlockSeparator, parts);
    }

    /// <summary>Words in <see cref="PlainText"/> plus code-block words weighted at one third.</summary>
    public static int WordCount(ContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var codeWords = 0;
        foreach (var block in Blocks(document))
        {
            foreach (var nodes in NodeLists(block))
            {
                codeWords += CodeWords(nodes);
            }
        }

        return CountWords(PlainText(document)) + (int)Math.Round(codeWords / 3.0, MidpointRounding.AwayFromZero);
    }

    public static int ReadingMinutes(ContentDocument document, int wordsPerMinute = 225)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordsPerMinute);
        return Math.Max(1, (int)Math.Ceiling(WordCount(document) / (double)wordsPerMinute));
    }

    /// <summary>The first prose paragraph (falling back to all plain text), cut on a word boundary with "…" when truncated.</summary>
    public static string Excerpt(ContentDocument document, int maxCharacters = 160)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 2);

        var source = FirstProse(document) ?? PlainText(document);
        var text = string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var room = maxCharacters - 1;
        var cut = text.LastIndexOf(' ', room);
        // A space exactly at the cut point means the preceding word is complete.
        var end = cut > 0 ? cut : room;
        return text[..end].TrimEnd() + "…";
    }

    private static string? FirstProse(ContentDocument document)
    {
        foreach (var block in Blocks(document))
        {
            if (block is not RichTextBlock rich || rich.Nodes.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var node in rich.Nodes)
            {
                if (node is { Kind: RichTextNodeKind.Paragraph or RichTextNodeKind.Quote or RichTextNodeKind.List })
                {
                    var text = NodeText(node);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return null;
    }

    private static ImmutableArray<ContentBlock> Blocks(ContentDocument document) =>
        document.Blocks.IsDefault ? [] : document.Blocks;

    private static IEnumerable<ImmutableArray<RichTextNode>> NodeLists(ContentBlock block) => block switch
    {
        RichTextBlock b => [b.Nodes],
        CalloutBlock b => [b.Nodes],
        _ => [],
    };

    private static string BlockText(ContentBlock block)
    {
        switch (block)
        {
            case RichTextBlock rich:
                return NodesText(rich.Nodes);
            case CalloutBlock callout:
                return Join(BlockSeparator, callout.Title, NodesText(callout.Nodes));
            case GalleryBlock gallery:
                var captions = gallery.Images.IsDefaultOrEmpty
                    ? []
                    : gallery.Images.Where(i => i is not null).Select(i => i.Caption);
                return Join("\n", [gallery.Title, .. captions]);
            case CtaBlock cta:
                return Join("\n", cta.Heading, cta.Body);
            default:
                return "";
        }
    }

    private static string Join(string separator, params IEnumerable<string?> parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string NodesText(ImmutableArray<RichTextNode> nodes) =>
        nodes.IsDefaultOrEmpty
            ? ""
            : Join(BlockSeparator, nodes.Select(NodeText));

    private static string NodeText(RichTextNode? node)
    {
        if (node is null)
        {
            return "";
        }

        var children = node.Children.IsDefaultOrEmpty ? [] : node.Children;
        return node.Kind switch
        {
            RichTextNodeKind.Text or RichTextNodeKind.InlineCode => node.Text ?? "",
            RichTextNodeKind.CodeBlock => "",
            RichTextNodeKind.Image => node.Caption ?? "",
            RichTextNodeKind.List or RichTextNodeKind.Table => Join("\n", children.Select(NodeText)),
            RichTextNodeKind.Quote => Join(BlockSeparator, children.Select(NodeText)),
            RichTextNodeKind.TableRow => Join(" ", children.Select(NodeText)),
            RichTextNodeKind.ListItem => ListItemText(children),
            _ => string.Concat(children.Select(NodeText)),
        };
    }

    // List items may mix inline runs with nested paragraphs/lists; keep blocks on separate lines.
    private static string ListItemText(ImmutableArray<RichTextNode> children)
    {
        var sb = new StringBuilder();
        foreach (var child in children)
        {
            var isBlock = child is { Kind: RichTextNodeKind.Paragraph or RichTextNodeKind.List };
            if (isBlock && sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(NodeText(child));
            if (isBlock)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString().Trim('\n');
    }

    private static int CodeWords(ImmutableArray<RichTextNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            return 0;
        }

        var total = 0;
        foreach (var node in nodes)
        {
            if (node is null)
            {
                continue;
            }

            if (node.Kind == RichTextNodeKind.CodeBlock)
            {
                total += CountWords(node.Text);
            }
            else
            {
                total += CodeWords(node.Children);
            }
        }

        return total;
    }

    private static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
