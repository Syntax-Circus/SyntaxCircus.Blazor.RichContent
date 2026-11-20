using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Rendering;

/// <summary>
/// Server-renders a stored <see cref="ContentDocument"/>. Blocks that fail validation are skipped (and logged as a warning)
/// so one bad block does not blank a published post; document-level errors do not suppress any block.
/// </summary>
public partial class ContentDocumentView
{
    private bool _render;
    private ImmutableArray<ContentBlock> _blocks = [];
    private object[] _keys = [];
    private int _urlCharacters = RichContentLimits.Default.UrlCharacters;
    private IReadOnlyDictionary<RichTextNode, string> _headingIds = new Dictionary<RichTextNode, string>();

    [Inject] private ContentBlockRegistry Registry { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Parameter, EditorRequired] public ContentDocument Document { get; set; } = default!;

    [Parameter] public RichContentLimits? Limits { get; set; }

    protected override void OnParametersSet()
    {
        _render = false;
        _blocks = [];
        if (Document is null)
        {
            return;
        }

        _urlCharacters = (Limits ?? RichContentLimits.Default).UrlCharacters;
        var all = Document.Blocks.IsDefault ? [] : Document.Blocks;
        var errors = RichContentRules.Validate(Document, Limits);
        var failing = errors.Where(e => e.BlockIndex is not null).Select(e => e.BlockIndex!.Value).ToHashSet();
        _blocks = [.. all.Where((_, index) => !failing.Contains(index))];
        if (_blocks.Length != all.Length)
        {
            LogSkipped(errors.Where(e => e.BlockIndex is not null));
        }

        _keys = [.. _blocks.Select((b, index) =>
            b.Id is not null && _blocks.Count(o => string.Equals(o.Id, b.Id, StringComparison.Ordinal)) == 1 ? (object)b.Id : index)];
        _headingIds = HeadingIdAllocator.Allocate(_blocks.Select(NodesOf));
        _render = true;
    }

    // Block ids and error codes only; never block content.
    private void LogSkipped(IEnumerable<RichContentError> errors)
    {
        var logger = (ILogger<ContentDocumentView>?)Services.GetService(typeof(ILogger<ContentDocumentView>));
        if (logger is null)
        {
            return;
        }

        var summary = string.Join(", ", errors.Select(e => $"{e.BlockId}:{e.Code}").Distinct(StringComparer.Ordinal));
        logger.LogWarning("Skipped rich content blocks that failed validation: {Skipped}", summary);
    }

    private static ImmutableArray<RichTextNode> NodesOf(ContentBlock block) => block switch
    {
        RichTextBlock rich => rich.Nodes,
        CalloutBlock callout => callout.Nodes,
        _ => [],
    };

    private static Dictionary<string, object?> ExtensionParameters(ExtensionBlock extension) => new() { ["Data"] = extension.Data };
}
