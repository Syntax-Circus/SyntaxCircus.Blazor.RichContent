using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Rendering;

/// <summary>Server-renders a stored <see cref="ContentDocument"/>. A document that fails validation renders nothing.</summary>
public partial class ContentDocumentView
{
    private bool _render;
    private ImmutableArray<ContentBlock> _blocks = [];
    private IReadOnlyDictionary<RichTextNode, string> _headingIds = new Dictionary<RichTextNode, string>();

    [Inject] private ContentBlockRegistry Registry { get; set; } = default!;

    [Parameter, EditorRequired] public ContentDocument Document { get; set; } = default!;

    [Parameter] public RichContentLimits? Limits { get; set; }

    protected override void OnParametersSet()
    {
        _render = false;
        _blocks = [];
        if (Document is null || RichContentRules.Validate(Document, Limits).Count > 0)
        {
            return;
        }

        _blocks = Document.Blocks.IsDefault ? [] : Document.Blocks;
        _headingIds = HeadingIdAllocator.Allocate(_blocks.Select(NodesOf));
        _render = true;
    }

    private static ImmutableArray<RichTextNode> NodesOf(ContentBlock block) => block switch
    {
        RichTextBlock rich => rich.Nodes,
        CalloutBlock callout => callout.Nodes,
        _ => [],
    };

    private static Dictionary<string, object?> ExtensionParameters(ExtensionBlock extension) => new() { ["Data"] = extension.Data };
}
