using System.Collections.Immutable;
using System.Text;
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
        var replaced = _parts.Count == 0
            || !incoming.AsSpan().SequenceEqual(_nodes.AsSpan(), ReferenceEqualityComparer.Instance);
        _nodes = incoming;
        if (replaced)
        {
            Plan();
        }
    }

    // Parts keep their components where the new plan has a part of the same kind in the same turn, so that a surface
    // can compare its new nodes with what it holds.
    private void Plan()
    {
        var previous = _parts.GroupBy(part => part.Kind).ToDictionary(group => group.Key, group => new Queue<Part>(group));
        _parts = [];
        foreach (var segment in SurfacePlanner.Plan(_nodes))
        {
            _parts.Add(Reuse(segment.Kind, segment.Count));
        }

        // Somewhere to type, which stores nothing until something is typed.
        if (_parts.Count == 0)
        {
            _parts.Add(Reuse(SegmentKind.Surface, 0));
        }

        Part Reuse(SegmentKind kind, int count)
        {
            if (!previous.TryGetValue(kind, out var same) || !same.TryDequeue(out var part))
            {
                return new(kind, count);
            }

            part.Count = count;
            return part;
        }
    }

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
