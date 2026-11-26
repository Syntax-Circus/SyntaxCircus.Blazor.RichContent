using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>
/// Which validation errors belong to which top-level node, so that Write mode can show them beside its surface or
/// card. The rules report only the block, so each node is validated on its own. What only a whole block or document
/// can break, such as too many nodes between them, belongs to no node.
/// </summary>
internal static class NodeErrors
{
    private sealed record Entry(RichContentLimits Limits, IReadOnlyList<string> Messages);

    // Nodes are immutable, and an unchanged one keeps its object, so each is validated once.
    private static readonly ConditionalWeakTable<RichTextNode, Entry> Cache = [];

    public static IReadOnlyList<string> For(RichTextNode node, RichContentLimits limits)
    {
        if (node is null)
        {
            return [];
        }

        if (!Cache.TryGetValue(node, out var entry) || !entry.Limits.Equals(limits))
        {
            var alone = new ContentDocument(ContentDocument.CurrentSchemaVersion, [new RichTextBlock("node", [node])]);
            entry = new(limits, [.. RichContentRules.Validate(alone, limits).Select(error => error.Message).Distinct()]);
            Cache.AddOrUpdate(node, entry);
        }

        return entry.Messages;
    }

    /// <summary>The messages of a run of nodes, each once.</summary>
    public static IReadOnlyList<string> For(ImmutableArray<RichTextNode> nodes, RichContentLimits limits) =>
        nodes.IsDefaultOrEmpty ? [] : [.. nodes.SelectMany(node => For(node, limits)).Distinct()];
}
