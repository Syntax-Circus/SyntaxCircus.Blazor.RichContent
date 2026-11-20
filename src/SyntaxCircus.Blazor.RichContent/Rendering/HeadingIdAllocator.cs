using System.Collections.Immutable;
using System.Text;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Rendering;

/// <summary>Assigns slug ids to headings in document order, de-duplicating with -2, -3 suffixes.</summary>
internal static class HeadingIdAllocator
{
    private const int MaxDepth = 32;

    public static IReadOnlyDictionary<RichTextNode, string> Allocate(IEnumerable<ImmutableArray<RichTextNode>> roots)
    {
        var ids = new Dictionary<RichTextNode, string>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!root.IsDefault)
            {
                Walk(root, 0, ids, used);
            }
        }

        return ids;
    }

    private static void Walk(ImmutableArray<RichTextNode> nodes, int depth, Dictionary<RichTextNode, string> ids, HashSet<string> used)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (node is null)
            {
                continue;
            }

            if (node.Kind == RichTextNodeKind.Heading)
            {
                var baseId = Slugify(PlainText(node, 0));
                var id = baseId;
                for (var n = 2; !used.Add(id); n++)
                {
                    id = baseId + "-" + n;
                }

                ids[node] = id;
            }

            if (!node.Children.IsDefaultOrEmpty)
            {
                Walk(node.Children, depth + 1, ids, used);
            }
        }
    }

    private static string PlainText(RichTextNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            return "";
        }

        var text = new StringBuilder(node.Text);
        if (!node.Children.IsDefaultOrEmpty)
        {
            foreach (var child in node.Children)
            {
                if (child is not null)
                {
                    text.Append(PlainText(child, depth + 1));
                }
            }
        }

        return text.ToString();
    }

    private static string Slugify(string text)
    {
        var slug = new StringBuilder();
        var pendingDash = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingDash = false;
                slug.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }
        }

        return slug.Length == 0 ? "section" : slug.ToString();
    }
}
