using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>One node list in Write mode: writing surfaces, with the nodes they cannot edit between them.</summary>
public partial class WritingBlock
{
    private const int PreviewCharacters = 200;

    // A stretch of the node list. The object is the component key, so it outlives edits that change its length.
    private sealed class Part(SegmentKind kind, int count)
    {
        public SegmentKind Kind { get; } = kind;

        public int Count { get; set; } = count;
    }

    private List<Part> _parts = [];

    // The latest nodes: what was passed in, with every edit since.
    private ImmutableArray<RichTextNode> _nodes = [];
    private ImmutableArray<RichTextNode> _parameter;

    [Parameter] public ImmutableArray<RichTextNode> Nodes { get; set; } = [];

    [Parameter] public EventCallback<ImmutableArray<RichTextNode>> NodesChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Label { get; set; } = "Rich text";

    protected override void OnParametersSet()
    {
        var incoming = Nodes.IsDefault ? [] : Nodes;

        // The same value as last time is no news. It may be older than the edits made since, if the host has not
        // passed them back down yet.
        if (_parts.Count > 0 && incoming == _parameter)
        {
            return;
        }

        _parameter = incoming;
        var before = _nodes;
        _nodes = incoming;
        if (_parts.Count == 0 || !SameObjects(incoming.AsSpan(), before.AsSpan()))
        {
            Plan(before);
        }
    }

    // A part, and so its component, goes to the new segment that holds the same nodes, wherever that now is: the
    // surface then keeps its text, caret, undo history and anything not yet delivered. The segments left over take
    // the remaining parts of their kind in order, and their surfaces compare the new nodes with what they hold.
    private void Plan(ImmutableArray<RichTextNode> before)
    {
        var unused = new List<(Part Part, ImmutableArray<RichTextNode> Nodes)>(_parts.Count);
        var start = 0;
        foreach (var part in _parts)
        {
            unused.Add((part, before.Slice(start, part.Count)));
            start += part.Count;
        }

        var segments = SurfacePlanner.Plan(_nodes).ToList();
        // Somewhere to type, which stores nothing until something is typed.
        if (segments.Count == 0)
        {
            segments.Add(new(SegmentKind.Surface, 0, 0));
        }

        var parts = new Part?[segments.Count];
        // The same objects first, so that of two equal stretches each keeps its own.
        Match((held, now, _) => SameObjects(held.AsSpan(), now.AsSpan()));
        // Then the same values in new objects, as after a reload. Only a surface's nodes are known to be shallow
        // enough to serialize, and only a surface has anything to lose.
        Match((held, now, kind) => kind == SegmentKind.Surface && string.Equals(Json(held), Json(now), StringComparison.Ordinal));
        Match((_, _, _) => true);

        _parts = [.. segments.Select((segment, index) => parts[index] ?? new Part(segment.Kind, segment.Count))];

        void Match(Func<ImmutableArray<RichTextNode>, ImmutableArray<RichTextNode>, SegmentKind, bool> matches)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                if (parts[index] is not null)
                {
                    continue;
                }

                var segment = segments[index];
                var nodes = Slice(segment.Start, segment.Count);
                var found = unused.FindIndex(
                    old => old.Part.Kind == segment.Kind && matches(old.Nodes, nodes, segment.Kind));
                if (found >= 0)
                {
                    parts[index] = unused[found].Part;
                    parts[index]!.Count = segment.Count;
                    unused.RemoveAt(found);
                }
            }
        }
    }

    private static bool SameObjects(ReadOnlySpan<RichTextNode> left, ReadOnlySpan<RichTextNode> right) =>
        left.Length == right.Length && left.SequenceEqual(right, ReferenceEqualityComparer.Instance);

    private static string Json(ImmutableArray<RichTextNode> nodes) =>
        JsonSerializer.Serialize(nodes, ContentDocumentSerializer.Options);

    private ImmutableArray<RichTextNode> Slice(int start, int count) => _nodes.Slice(start, count);

    // Nothing here may await first: see RichTextSurface.ReceiveEdit.
    private Task SurfaceChanged(Part part, ImmutableArray<RichTextNode> changed)
    {
        var position = _parts.IndexOf(part);
        if (position < 0)
        {
            return Task.CompletedTask;
        }

        var start = _parts.Take(position).Sum(earlier => earlier.Count);
        var after = start + part.Count;
        _nodes = [.. _nodes.AsSpan(0, start), .. changed, .. _nodes.AsSpan(after, _nodes.Length - after)];
        part.Count = changed.Length;
        return NodesChanged.InvokeAsync(_nodes);
    }

    private static string NameOf(SegmentKind kind) => kind switch
    {
        SegmentKind.Code => "Code block",
        SegmentKind.Image => "Image",
        SegmentKind.Table => "Table",
        _ => "Preserved content",
    };

    // The start of the text, found without recursion: a locked node can be nested arbitrarily deeply.
    private static string Preview(ImmutableArray<RichTextNode> nodes)
    {
        var text = new StringBuilder();
        var pending = new Stack<RichTextNode?>(nodes.Reverse());
        while (text.Length < PreviewCharacters && pending.TryPop(out var node))
        {
            if (node is null)
            {
                continue;
            }

            if ((node.Text ?? node.Alt) is { Length: > 0 } own)
            {
                text.Append(text.Length > 0 ? " " : "").Append(own);
            }

            if (!node.Children.IsDefaultOrEmpty)
            {
                for (var index = node.Children.Length - 1; index >= 0; index--)
                {
                    pending.Push(node.Children[index]);
                }
            }
        }

        return text.Length <= PreviewCharacters ? text.ToString() : text.ToString(0, PreviewCharacters);
    }
}
