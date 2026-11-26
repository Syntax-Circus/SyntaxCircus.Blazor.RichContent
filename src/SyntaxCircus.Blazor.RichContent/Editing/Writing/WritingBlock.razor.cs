using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>
/// One node list in Write mode: cards for the code blocks, images, tables and anything that cannot be edited exactly,
/// with a writing surface before, between and after them. A surface with no prose to hold is somewhere to type,
/// which stores nothing until something is typed there.
/// </summary>
public partial class WritingBlock
{
    // A stretch of the node list. The object is the component key, so it outlives edits that change its length.
    private sealed class Part(WritingBlock owner, SegmentKind kind) : IInsertPlace, IWritingPart
    {
        public SegmentKind Kind { get; } = kind;

        public bool IsGone { get; set; }

        public int Count { get; set; }

        /// <summary>The surface that shows this part, once drawn.</summary>
        public RichTextSurface? Surface { get; set; }

        public IReadOnlyList<InsertChoice> Choices => owner.InsertChoices;

        public Task InsertAsync(int choice, int nodesBefore) => owner.InsertAsync(this, choice, nodesBefore);

        public void Refuse(string reason) => owner.Refuse(this, reason);
    }

    private List<Part> _parts = [];

    // The latest nodes: what was passed in, with every edit since.
    private ImmutableArray<RichTextNode> _nodes = [];
    private ImmutableArray<RichTextNode> _parameter;

    // The node Insert made last, whose card takes the focus when it appears.
    private RichTextNode? _inserted;

    // What was last asked for and not done, shown where it was asked.
    private (Part At, string Reason)? _refusal;

    // Absent for a writing block used outside a ContentDocumentEditor.
    [CascadingParameter] private WritingSession? Session { get; set; }

    [Inject] private RichContentEditorOptions Options { get; set; } = default!;

    [Parameter] public ImmutableArray<RichTextNode> Nodes { get; set; } = [];

    /// <summary>
    /// Raised for each edit. A handler must store the nodes before its first incomplete await, so that a click made
    /// after typing sees them.
    /// </summary>
    [Parameter] public EventCallback<ImmutableArray<RichTextNode>> NodesChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Label { get; set; } = "Rich text";

    [Parameter] public int MaxDepth { get; set; } = RichContentLimits.Default.Depth;

    /// <summary>What Insert offers in these surfaces. With nothing, Insert is not offered.</summary>
    [Parameter] public IReadOnlyList<InsertChoice> InsertChoices { get; set; } = [];

    /// <summary>
    /// Raised to insert a block before the node at <c>NodeIndex</c>, which splits the rich text block there (see
    /// <c>ContentDocumentOperations.SplitRichText</c>). With the index between two nodes, this component keeps only
    /// the nodes before it, so a handler must make that split and pass those nodes back. At either end it keeps them
    /// all, and the block goes beside it.
    /// </summary>
    [Parameter] public EventCallback<(int NodeIndex, ContentBlock Block)> InsertBlockAt { get; set; }

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
        _refusal = null;
        if (_parts.Count == 0 || !SameObjects(incoming.AsSpan(), _nodes.AsSpan()))
        {
            Commit(incoming, Plan(incoming, replaced: true));
        }
        else
        {
            _nodes = incoming;
        }
    }

    // Surfaces and cards by turns, beginning and ending with a surface. A surface with no prose between two cards,
    // before the first or after the last has no nodes.
    private static List<Segment> Layout(ImmutableArray<RichTextNode> nodes)
    {
        var layout = new List<Segment>();
        foreach (var segment in SurfacePlanner.Plan(nodes))
        {
            if (segment.Kind != SegmentKind.Surface && layout is not [.., { Kind: SegmentKind.Surface }])
            {
                layout.Add(new(SegmentKind.Surface, segment.Start, 0));
            }

            layout.Add(segment);
        }

        if (layout is not [.., { Kind: SegmentKind.Surface }])
        {
            layout.Add(new(SegmentKind.Surface, nodes.Length, 0));
        }

        return layout;
    }

    // A part, and so its component, goes to the new segment that holds the same nodes, wherever that now is: the
    // surface then keeps its text, caret, undo history and anything not yet delivered.
    //
    // When the host replaced the nodes, the segments left over also take the parts of their kind in order, and their
    // surfaces compare the new nodes with what they hold. When this component rearranged the nodes itself, those
    // segments get new parts instead. A surface may not have heard of the change before it sends its next edit, and
    // what an old part sends is ignored, where it would otherwise be put back over the change.
    private (Part Part, int Count)[] Plan(ImmutableArray<RichTextNode> nodes, bool replaced)
    {
        var unused = new List<(Part Part, ImmutableArray<RichTextNode> Nodes)>(_parts.Count);
        var start = 0;
        foreach (var part in _parts)
        {
            unused.Add((part, _nodes.Slice(start, part.Count)));
            start += part.Count;
        }

        var segments = Layout(nodes);
        var parts = new Part?[segments.Count];
        // The same objects first, so that of two equal stretches each keeps its own.
        Match((held, now, _) => SameObjects(held.AsSpan(), now.AsSpan()));
        if (replaced)
        {
            // Then the same values in new objects, as after a reload. A locked node may be too deep to serialize,
            // and its card has nothing to lose.
            Match((held, now, kind) => kind != SegmentKind.Locked && string.Equals(Json(held), Json(now), StringComparison.Ordinal));
            // A surface with prose is rather given other prose than turned into an empty place.
            Match((held, now, _) => held.IsEmpty == now.IsEmpty);
            Match((_, _, _) => true);
        }

        return [.. segments.Select((segment, index) => (parts[index] ?? new Part(this, segment.Kind), segment.Count))];

        void Match(Func<ImmutableArray<RichTextNode>, ImmutableArray<RichTextNode>, SegmentKind, bool> matches)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                if (parts[index] is not null)
                {
                    continue;
                }

                var segment = segments[index];
                var now = nodes.Slice(segment.Start, segment.Count);
                bool IsMatch((Part Part, ImmutableArray<RichTextNode> Nodes) old) =>
                    old.Part.Kind == segment.Kind && matches(old.Nodes, now, segment.Kind);

                // Of those that would do, one that shows what could not be saved, so that it is not the one left out.
                var found = replaced ? unused.FindIndex(old => IsMatch(old) && ShowsUnsaved(old.Part)) : -1;
                if (found < 0)
                {
                    found = unused.FindIndex(IsMatch);
                }

                if (found >= 0)
                {
                    parts[index] = unused[found].Part;
                    unused.RemoveAt(found);
                }
            }
        }
    }

    private void Commit(ImmutableArray<RichTextNode> nodes, (Part Part, int Count)[] plan)
    {
        _nodes = nodes;
        _parts = [.. plan.Select(entry => entry.Part)];
        foreach (var (part, count) in plan)
        {
            part.Count = count;
        }
    }

    private bool ShowsUnsaved(Part part) => part.Surface is { HasRejectedEdit: true } || Session?.HasRejectedIn(part) == true;

    private void Refuse(Part at, string reason)
    {
        _refusal = (at, reason);
        StateHasChanged();
    }

    // Moves nodes about on the author's behalf. False, changing nothing, if that would redraw or drop a surface or card
    // which shows something that could not be converted, and so exists only on screen. The reason is then shown at the
    // part that asked. The card the author removes goes with whatever it shows: they were asked first.
    private bool TryRearrange(ImmutableArray<RichTextNode> nodes, Part asked, Part? removed = null)
    {
        var plan = Plan(nodes, replaced: false);
        var kept = plan.Select(entry => entry.Part).ToHashSet();
        if (_parts.Exists(part => !kept.Contains(part) && !ReferenceEquals(part, removed) && ShowsUnsaved(part)))
        {
            _refusal = (asked, WriteModeMessages.RearrangeRefused);
            return false;
        }

        _refusal = null;

        // The components of the parts left out stay alive until a render, which can be held back. From here on
        // nothing they send can be stored, so none of it may be acknowledged.
        foreach (var part in _parts.Where(part => !kept.Contains(part)))
        {
            part.IsGone = true;
        }

        Commit(nodes, plan);
        return true;
    }

    private static bool SameObjects(ReadOnlySpan<RichTextNode> left, ReadOnlySpan<RichTextNode> right) =>
        left.Length == right.Length && left.SequenceEqual(right, ReferenceEqualityComparer.Instance);

    private static string Json(ImmutableArray<RichTextNode> nodes) =>
        JsonSerializer.Serialize(nodes, ContentDocumentSerializer.Options);

    private ImmutableArray<RichTextNode> Slice(int start, int count) => _nodes.Slice(start, count);

    private IReadOnlyList<string> ErrorsOf(ImmutableArray<RichTextNode> nodes) =>
        Session is { } session ? NodeErrors.For(nodes, session.Limits) : [];

    // Where a part's nodes begin, or -1 for a part that is no longer shown.
    private int StartOf(Part part)
    {
        var position = _parts.IndexOf(part);
        return position < 0 ? -1 : _parts.Take(position).Sum(earlier => earlier.Count);
    }

    // Nothing here may await first: see ScriptEditorBase.ReceiveEdit.
    private Task SurfaceChanged(Part part, ImmutableArray<RichTextNode> changed)
    {
        var start = StartOf(part);
        if (start < 0)
        {
            return Task.CompletedTask;
        }

        var after = start + part.Count;
        _nodes = [.. _nodes.AsSpan(0, start), .. changed, .. _nodes.AsSpan(after, _nodes.Length - after)];
        part.Count = changed.Length;
        return NodesChanged.InvokeAsync(_nodes);
    }

    // Nor here: the edits of a code card and of table cells come this way.
    private Task CardChanged(Part part, RichTextNode node)
    {
        var start = StartOf(part);
        if (start < 0)
        {
            return Task.CompletedTask;
        }

        _nodes = _nodes.SetItem(start, node);
        return NodesChanged.InvokeAsync(_nodes);
    }

    private bool HoldsSomething(Part part) =>
        StartOf(part) is var start && start >= 0
        && (part.Kind == SegmentKind.Locked || ShowsUnsaved(part) || !WritingContent.IsEmpty(_nodes[start]));

    private Task MoveCard(Part part, int delta)
    {
        var start = StartOf(part);
        if (Disabled || start < 0 || start + delta < 0 || start + delta >= _nodes.Length)
        {
            return Task.CompletedTask;
        }

        return TryRearrange(RichTextEditorOperations.Move(_nodes, start, delta), part)
            ? NodesChanged.InvokeAsync(_nodes)
            : Task.CompletedTask;
    }

    private Task RemoveCard(Part part)
    {
        var start = StartOf(part);
        return !Disabled && start >= 0 && TryRearrange(_nodes.RemoveAt(start), part, removed: part)
            ? NodesChanged.InvokeAsync(_nodes)
            : Task.CompletedTask;
    }

    private async Task InsertAsync(Part part, int index, int nodesBefore)
    {
        var start = StartOf(part);
        if (Disabled || start < 0 || index < 0 || index >= InsertChoices.Count)
        {
            return;
        }

        var choice = InsertChoices[index];
        if (choice.NeedsMedia && Options.MediaPicker is null)
        {
            return;
        }

        var at = start + Math.Clamp(nodesBefore, 0, part.Count);
        Task raised;
        if (choice.NodeKind is { } kind)
        {
            var node = RichTextEditorOperations.Create(kind);
            if (!TryRearrange(_nodes.Insert(at, node), part))
            {
                StateHasChanged();
                return;
            }

            _inserted = node;
            raised = NodesChanged.InvokeAsync(_nodes);
        }
        else if (choice.CreateBlock is { } create && InsertBlockAt.HasDelegate)
        {
            // Asked before anything is let go of here: a new block can have other blocks drawn again.
            if (Session?.RefusesNewBlock() == true)
            {
                return;
            }

            // Between two nodes, the ones from here on go to a block of their own, after the new one. This lets go
            // of them first, so that nothing a surface sends meanwhile can bring them back here.
            if (at > 0 && at < _nodes.Length && !TryRearrange([.. _nodes.AsSpan(0, at)], part))
            {
                StateHasChanged();
                return;
            }

            raised = InsertBlockAt.InvokeAsync((at, create()));
        }
        else
        {
            return;
        }

        // This did not come in as an event of this component.
        StateHasChanged();
        await raised;
    }

    private static string NameOf(SegmentKind kind) => kind switch
    {
        SegmentKind.Code => "Code block",
        SegmentKind.Image => "Image",
        SegmentKind.Table => "Table",
        _ => "Preserved content",
    };
}
