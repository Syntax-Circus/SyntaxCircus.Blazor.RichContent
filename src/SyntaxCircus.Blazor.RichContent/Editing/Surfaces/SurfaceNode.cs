using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// A node as the browser's writing surface sends and receives it. It carries only what a surface can edit.
/// A null <see cref="Children"/> (no children property) differs from an empty one, and a null <see cref="Ordered"/>
/// differs from false; both differences show in the stored JSON.
/// </summary>
internal sealed record SurfaceNode(string Kind, string? Text = null, string? Url = null,
    int? Level = null, bool? Ordered = null, SurfaceNode[]? Children = null)
{
    // The same names the serializer writes. Kinds shown as cards have no wire form.
    private static readonly FrozenDictionary<string, RichTextNodeKind> Kinds = new[]
    {
        RichTextNodeKind.Paragraph, RichTextNodeKind.Heading, RichTextNodeKind.Text, RichTextNodeKind.Emphasis,
        RichTextNodeKind.Strong, RichTextNodeKind.InlineCode, RichTextNodeKind.Link, RichTextNodeKind.List,
        RichTextNodeKind.ListItem, RichTextNodeKind.Quote,
    }.ToFrozenDictionary(kind => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()), StringComparer.Ordinal);

    private static readonly FrozenDictionary<RichTextNodeKind, string> Names =
        Kinds.ToFrozenDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>
    /// The wire shape of a node a surface can edit. Other fields and unknown properties are not carried, so keep the
    /// original node for anything the author has not changed.
    /// </summary>
    public static SurfaceNode From(RichTextNode node)
    {
        if (!Names.TryGetValue(node.Kind, out var kind))
        {
            throw new ArgumentException($"A writing surface cannot show a {node.Kind} node.", nameof(node));
        }

        return new(kind, node.Text, node.Url, node.HeadingLevel, node.Ordered,
            node.Children.IsDefault ? null : [.. node.Children.Select(From)]);
    }

    /// <exception cref="FormatException">
    /// A kind is missing or unknown, a child is null, or the tree is too deep to be stored.
    /// </exception>
    public RichTextNode ToNode() => ToNode(depth: 1);

    // Recursive, so the depth is bounded before it can exhaust the stack. The browser's input is not trusted.
    private RichTextNode ToNode(int depth)
    {
        if (depth > ContentDocumentSerializer.Options.MaxDepth)
        {
            throw new FormatException("The nodes are nested too deeply.");
        }

        if (Kind is null || !Kinds.TryGetValue(Kind, out var kind))
        {
            throw new FormatException($"Unknown node kind '{Kind}'.");
        }

        ImmutableArray<RichTextNode> children = Children is null
            ? default
            : [.. Children.Select(child => child?.ToNode(depth + 1) ?? throw new FormatException("A child node is null."))];

        return new(kind, Text, Url, Level, children, Ordered);
    }
}

/// <summary>A top-level node of a surface and the key that identifies it between patches.</summary>
internal sealed record KeyedNode(string Key, SurfaceNode Node);

/// <summary>
/// An edit from the browser: every top-level key now in the surface, in order, and the nodes for the keys that are
/// new or changed. <paramref name="Generation"/> is the <see cref="SurfaceBuffer.Generation"/> it was made against.
/// </summary>
internal sealed record SurfacePatch(int Generation, string[] Order, KeyedNode[] Changed);
