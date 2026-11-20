using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Rendering;

/// <summary>
/// Renders rich-text nodes as HTML. Defensive by design: stored content may be odd, so unknown shapes
/// are skipped or flattened rather than thrown on, and links that fail <see cref="RichContentUrl.IsSafe"/> lose their anchor.
/// </summary>
public sealed class RichTextView : ComponentBase
{
    private const int MaxDepth = 32;

    private IReadOnlyDictionary<RichTextNode, string>? _headingIds;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Parameter, EditorRequired] public ImmutableArray<RichTextNode> Nodes { get; set; }

    /// <summary>Precomputed heading ids (reference-keyed) so ids stay unique across a whole document. Computed locally when absent.</summary>
    [Parameter] public IReadOnlyDictionary<RichTextNode, string>? HeadingIds { get; set; }

    /// <summary>Maximum link length accepted; longer links lose their anchor. Matches <see cref="RichContentLimits.UrlCharacters"/>.</summary>
    [Parameter] public int UrlCharacters { get; set; } = RichContentLimits.Default.UrlCharacters;

    protected override void OnParametersSet() =>
        _headingIds = HeadingIds ?? HeadingIdAllocator.Allocate([Nodes]);

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Nodes.IsDefaultOrEmpty)
        {
            return;
        }

        var context = new Context(
            (IRichContentMediaResolver?)Services.GetService(typeof(IRichContentMediaResolver)),
            (ICodeHighlighter?)Services.GetService(typeof(ICodeHighlighter)));
        foreach (var node in Nodes)
        {
            Build(builder, node, 0, context);
        }
    }

    private sealed record Context(IRichContentMediaResolver? Resolver, ICodeHighlighter? Highlighter);

    private void BuildChildren(RenderTreeBuilder builder, RichTextNode node, int depth, Context context)
    {
        if (node.Children.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Build(builder, child, depth + 1, context);
        }
    }

    private void Build(RenderTreeBuilder builder, RichTextNode? node, int depth, Context context)
    {
        if (node is null || depth > MaxDepth)
        {
            return;
        }

        switch (node.Kind)
        {
            case RichTextNodeKind.Text:
                if (!string.IsNullOrEmpty(node.Text))
                {
                    builder.AddContent(0, node.Text);
                }
                break;
            case RichTextNodeKind.InlineCode:
                if (!string.IsNullOrEmpty(node.Text))
                {
                    builder.OpenElement(1, "code");
                    builder.AddContent(2, node.Text);
                    builder.CloseElement();
                }
                break;
            case RichTextNodeKind.CodeBlock:
                BuildCodeBlock(builder, node, context.Highlighter);
                break;
            case RichTextNodeKind.Image:
                if (node.Media is not null)
                {
                    ImageFigure.Build(builder, context.Resolver?.Resolve(node.Media), node.Alt, node.Caption);
                }
                break;
            case RichTextNodeKind.Link:
                BuildLink(builder, node, depth, context);
                break;
            case RichTextNodeKind.Heading:
                string? id = null;
                _headingIds?.TryGetValue(node, out id);
                BuildElement(builder, node, node.HeadingLevel switch { 2 => "h2", 3 => "h3", _ => "h4" }, depth, context, id);
                break;
            case RichTextNodeKind.Paragraph:
                BuildElement(builder, node, "p", depth, context);
                break;
            case RichTextNodeKind.Emphasis:
                BuildElement(builder, node, "em", depth, context);
                break;
            case RichTextNodeKind.Strong:
                BuildElement(builder, node, "strong", depth, context);
                break;
            case RichTextNodeKind.List:
                BuildElement(builder, node, node.Ordered == true ? "ol" : "ul", depth, context);
                break;
            case RichTextNodeKind.ListItem:
                BuildElement(builder, node, "li", depth, context);
                break;
            case RichTextNodeKind.Quote:
                BuildElement(builder, node, "blockquote", depth, context);
                break;
            case RichTextNodeKind.Table:
                BuildElement(builder, node, "table", depth, context);
                break;
            case RichTextNodeKind.TableRow:
                BuildElement(builder, node, "tr", depth, context);
                break;
            case RichTextNodeKind.TableCell:
                BuildElement(builder, node, node.Header == true ? "th" : "td", depth, context);
                break;
            default:
                // Unrecognised kind: keep any readable content, drop the wrapper.
                BuildChildren(builder, node, depth, context);
                break;
        }
    }

    private void BuildElement(RenderTreeBuilder builder, RichTextNode node, string tag, int depth, Context context, string? id = null)
    {
        builder.OpenElement(10, tag);
        if (id is not null)
        {
            builder.AddAttribute(11, "id", id);
        }

        builder.OpenRegion(12);
        BuildChildren(builder, node, depth, context);
        builder.CloseRegion();
        builder.CloseElement();
    }

    private void BuildLink(RenderTreeBuilder builder, RichTextNode node, int depth, Context context)
    {
        if (!RichContentUrl.IsSafe(node.Url, UrlCharacters))
        {
            BuildChildren(builder, node, depth, context);
            return;
        }

        builder.OpenElement(20, "a");
        builder.AddAttribute(21, "href", node.Url);
        if (IsExternal(node.Url!))
        {
            builder.AddAttribute(22, "rel", "noopener");
        }

        builder.OpenRegion(23);
        BuildChildren(builder, node, depth, context);
        builder.CloseRegion();
        builder.CloseElement();
    }

    private static void BuildCodeBlock(RenderTreeBuilder builder, RichTextNode node, ICodeHighlighter? highlighter)
    {
        var code = node.Text ?? "";
        builder.OpenElement(30, "pre");
        builder.OpenElement(31, "code");
        if (!string.IsNullOrEmpty(node.Language))
        {
            builder.AddAttribute(32, "class", "language-" + node.Language);
        }

        if (highlighter?.Highlight(code, node.Language) is { } markup)
        {
            builder.AddContent(33, markup);
        }
        else
        {
            builder.AddContent(34, code);
        }

        builder.CloseElement();
        builder.CloseElement();
    }

    /// <summary>Absolute <c>https:</c> links point off-site; root-relative, mailto and tel do not.</summary>
    internal static bool IsExternal(string url) => url.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
}
