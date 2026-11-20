using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Edits a list of top-level rich-text nodes with plain HTML controls.</summary>
public partial class RichTextEditor
{
    private static readonly RichTextNodeKind[] AddKinds =
    [
        RichTextNodeKind.Paragraph, RichTextNodeKind.Heading, RichTextNodeKind.List, RichTextNodeKind.Quote,
        RichTextNodeKind.CodeBlock, RichTextNodeKind.Table, RichTextNodeKind.Image,
    ];

    [Inject] private RichContentEditorOptions Options { get; set; } = default!;

    [Parameter] public ImmutableArray<RichTextNode> Nodes { get; set; } = [];

    [Parameter] public EventCallback<ImmutableArray<RichTextNode>> NodesChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Label { get; set; } = "Rich text";

    /// <summary>Optional content rendered between two nodes (receives the index of the node that follows), used for the insert-block menu.</summary>
    [Parameter] public RenderFragment<int>? BetweenNodes { get; set; }

    [Parameter] public int MaxDepth { get; set; } = RichContentLimits.Default.Depth;

    private ImmutableArray<RichTextNode> Items => Nodes.IsDefault ? [] : Nodes;

    private bool HasMediaPicker => Options.MediaPicker is not null;

    private Task Replace(int index, RichTextNode node) => NodesChanged.InvokeAsync(Items.SetItem(index, node));

    private Task Move(int index, int delta) => NodesChanged.InvokeAsync(RichTextEditorOperations.Move(Items, index, delta));

    private Task Remove(int index) => NodesChanged.InvokeAsync(RichTextEditorOperations.Remove(Items, index));

    private Task Add(RichTextNodeKind kind) =>
        NodesChanged.InvokeAsync(RichTextEditorOperations.Append(Items, RichTextEditorOperations.Create(kind)));
}
