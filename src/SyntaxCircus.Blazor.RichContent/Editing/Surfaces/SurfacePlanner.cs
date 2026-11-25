using System.Collections.Immutable;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>How Write mode shows a stretch of a node list: one writing surface, or one card.</summary>
internal enum SegmentKind { Surface, Code, Image, Table, Locked }

/// <summary>A run of <paramref name="Count"/> top-level nodes starting at <paramref name="Start"/>.</summary>
internal sealed record Segment(SegmentKind Kind, int Start, int Count);

/// <summary>Splits a node list into writing surfaces and cards.</summary>
internal static class SurfacePlanner
{
    /// <summary>
    /// Segments cover every node once, in order. Consecutive editable prose shares a surface; every card,
    /// including a locked one, holds exactly one node.
    /// </summary>
    public static IReadOnlyList<Segment> Plan(ImmutableArray<RichTextNode> nodes)
    {
        var segments = new List<Segment>();
        if (nodes.IsDefaultOrEmpty)
        {
            return segments;
        }

        for (var index = 0; index < nodes.Length; index++)
        {
            var kind = KindOf(nodes[index]);
            if (kind == SegmentKind.Surface && segments is [.., { Kind: SegmentKind.Surface } previous])
            {
                segments[^1] = previous with { Count = previous.Count + 1 };
            }
            else
            {
                segments.Add(new(kind, index, 1));
            }
        }

        return segments;
    }

    private static SegmentKind KindOf(RichTextNode? node) => node switch
    {
        null => SegmentKind.Locked,
        _ when SurfaceSupport.CanEdit(node) => SegmentKind.Surface,
        { Kind: RichTextNodeKind.CodeBlock } when SurfaceSupport.HasSupportedShape(node) => SegmentKind.Code,
        { Kind: RichTextNodeKind.Image } when SurfaceSupport.HasSupportedShape(node) => SegmentKind.Image,
        { Kind: RichTextNodeKind.Table } when SurfaceSupport.HasSupportedShape(node) => SegmentKind.Table,
        _ => SegmentKind.Locked,
    };
}
