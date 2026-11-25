using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// The nodes of one writing surface, by key. A patch rebuilds only the nodes it changes; every other node keeps its
/// original object, so its stored JSON, unknown properties included, cannot change.
/// </summary>
/// <param name="inline">True for a surface holding inline content (a table cell) instead of blocks of prose.</param>
internal sealed class SurfaceBuffer(bool inline = false)
{
    // A key with a null node is one the author emptied: nothing is stored for it, but the browser still has it.
    private Dictionary<string, RichTextNode?> _known = new(StringComparer.Ordinal);
    private ImmutableArray<RichTextNode> _nodes = [];
    private ImmutableArray<string> _nodeKeys = [];

    // Never restarts, so a key cannot come to mean another node. The browser must name its own keys differently.
    private long _lastKey;

    /// <summary>Counts the calls to <see cref="Reset"/>. A patch made against another generation cannot be applied.</summary>
    public int Generation { get; private set; }

    /// <summary>Replaces the contents with nodes a surface can edit, and returns them as the browser needs them.</summary>
    public IReadOnlyList<KeyedNode> Reset(ImmutableArray<RichTextNode> nodes)
    {
        nodes = nodes.IsDefault ? [] : nodes;
        var lastKey = _lastKey;
        KeyedNode[] keyed = [.. nodes.Select(node => new KeyedNode($"s{++lastKey}", SurfaceNode.From(node)))];
        var known = new Dictionary<string, RichTextNode?>(StringComparer.Ordinal);
        for (var index = 0; index < keyed.Length; index++)
        {
            known.Add(keyed[index].Key, nodes[index]);
        }

        _lastKey = lastKey;
        _known = known;
        _nodes = nodes;
        _nodeKeys = [.. keyed.Select(entry => entry.Key)];
        Generation++;
        return keyed;
    }

    /// <summary>
    /// Applies a patch and returns the surface's nodes. Applying changes that were already applied is harmless.
    /// A list, quote or link the author emptied is left out. A rejected patch changes nothing.
    /// </summary>
    /// <exception cref="FormatException">
    /// The patch is for another generation, is incomplete or inconsistent, names a key that is not known, has too
    /// many entries, or holds a node a surface could not edit again.
    /// </exception>
    public ImmutableArray<RichTextNode> Apply(SurfacePatch patch)
    {
        if (patch is not { Order: not null, Changed: not null })
        {
            throw new FormatException("The patch is incomplete.");
        }

        if (patch.Generation != Generation)
        {
            throw new FormatException($"The patch is for generation {patch.Generation}, not {Generation}.");
        }

        if (patch.Order.Length > RichContentLimits.Default.Nodes)
        {
            throw new FormatException("The patch has too many nodes.");
        }

        var changed = new Dictionary<string, SurfaceNode>(StringComparer.Ordinal);
        foreach (var entry in patch.Changed)
        {
            if (entry is not { Key.Length: > 0, Node: not null })
            {
                throw new FormatException("A changed entry is incomplete.");
            }

            if (!changed.TryAdd(entry.Key, entry.Node))
            {
                // The messages never repeat what the browser sent: it can be very long.
                throw new FormatException("A key is changed twice.");
            }
        }

        var next = new Dictionary<string, RichTextNode?>(StringComparer.Ordinal);
        var nodes = ImmutableArray.CreateBuilder<RichTextNode>();
        var nodeKeys = ImmutableArray.CreateBuilder<string>();
        foreach (var key in patch.Order)
        {
            if (string.IsNullOrEmpty(key) || next.ContainsKey(key))
            {
                throw new FormatException("A key is empty or used twice.");
            }

            var isKnown = _known.TryGetValue(key, out var node);
            if (changed.Remove(key, out var wire))
            {
                node = Rebuild(wire, node);
            }
            else if (!isKnown)
            {
                throw new FormatException("A key is not known and the patch has no node for it.");
            }

            next.Add(key, node);
            if (node is not null)
            {
                nodes.Add(node);
                nodeKeys.Add(key);
            }
        }

        if (changed.Count > 0)
        {
            throw new FormatException("A changed key is missing from the order.");
        }

        _known = next;
        _nodes = nodes.ToImmutable();
        _nodeKeys = nodeKeys.ToImmutable();
        return _nodes;
    }

    /// <summary>True when the surface holds these values, whether or not they are the same objects.</summary>
    public bool Matches(ImmutableArray<RichTextNode> nodes) =>
        string.Equals(Json(_nodes), Json(nodes.IsDefault ? [] : nodes), StringComparison.Ordinal);

    /// <summary>
    /// When the surface holds these values (see <see cref="Matches"/>), takes the new objects as its originals,
    /// keeping every key and the generation, and returns true. Otherwise changes nothing.
    /// </summary>
    public bool TryRebase(ImmutableArray<RichTextNode> nodes)
    {
        if (!Matches(nodes))
        {
            return false;
        }

        nodes = nodes.IsDefault ? [] : nodes;
        for (var index = 0; index < _nodeKeys.Length; index++)
        {
            _known[_nodeKeys[index]] = nodes[index];
        }

        _nodes = nodes;
        return true;
    }

    // Null when the author emptied the node.
    private RichTextNode? Rebuild(SurfaceNode wire, RichTextNode? previous)
    {
        if (WithoutEmptied(wire.ToNode()) is not { } node)
        {
            return null;
        }

        // An editable node must never come back as one the next plan would lock.
        if (!(inline ? SurfaceSupport.CanEditInline([node]) : SurfaceSupport.CanEdit(node)))
        {
            throw new FormatException($"A writing surface could not edit this {node.Kind} node again.");
        }

        return previous is not null && string.Equals(Json(previous), Json(node), StringComparison.Ordinal) ? previous : node;
    }

    // Inside out, so a container that held only emptied containers goes too. ToNode has bounded the depth.
    // Only a clean container is dropped. One that carries a field it must not have, or lacks one it needs, would
    // take that content with it, so the patch is rejected instead.
    private static RichTextNode? WithoutEmptied(RichTextNode node)
    {
        if (!node.Children.IsDefaultOrEmpty)
        {
            node = node with { Children = [.. node.Children.Select(WithoutEmptied).OfType<RichTextNode>()] };
        }

        if (!node.Children.IsDefaultOrEmpty || !SurfaceSupport.NeedsChildren(node.Kind))
        {
            return node;
        }

        return SurfaceSupport.HasSupportedFields(node)
            ? null
            : throw new FormatException($"An empty {node.Kind} node has fields it cannot have.");
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContentDocumentSerializer.Options);
}
